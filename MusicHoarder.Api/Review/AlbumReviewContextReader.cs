using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review;

internal sealed class AlbumReviewReadException(int statusCode, string code, string message, int? limit = null)
    : Exception(message)
{
    internal int StatusCode { get; } = statusCode;
    internal AlbumReviewError Error { get; } = new(code, message, limit);
}

/// <summary>Bounded, owner-scoped reads of existing evidence. No provider/builder dependencies.</summary>
internal static class AlbumReviewContextReader
{
    internal const int MaxTracks = 200;
    internal const int MaxEvidenceRows = 4000;
    internal const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ProviderJson = new() { PropertyNameCaseInsensitive = true };

    // Only these fields may leave the database via this API. In particular: no lyrics,
    // fingerprint, raw provider payload, last-written payload, or free-form exception text.
    private static readonly string[] TagFields =
    [
        "Title", "Artist", "Artists", "AlbumArtist", "Album", "Year", "TrackNumber", "DiscNumber",
        "TotalDiscs", "TotalTracks", "IsCompilation", "ReleaseTypePrimary", "ReleaseTypes",
        "MusicBrainzId", "MusicBrainzReleaseId", "MusicBrainzReleaseGroupId", "ArtistMusicBrainzIds",
        "AlbumArtistMusicBrainzId", "SpotifyId", "AcoustIdTrackId", "Isrc", "Genre", "ReleaseDate",
        "OriginalReleaseDate", "Label", "CatalogNumber", "Upc", "Composer", "Copyright", "ArtistSort", "AlbumArtistSort"
    ];
    private static readonly string[] OriginalFields =
    [
        "Title", "Artist", "Artists", "AlbumArtist", "Album", "Year", "TrackNumber", "DiscNumber",
        "TotalDiscs", "TotalTracks", "IsCompilation", "ReleaseTypePrimary", "ReleaseTypes",
        "MusicBrainzId", "SpotifyId", "Isrc"
    ];

    internal static async Task<AlbumReviewGroupsPage> SearchAsync(
        MusicHoarderDbContext db, Guid owner, string? from, string? query, string? status,
        int minRemaining, int maxRemaining, int offset, int limit, CancellationToken ct)
    {
        if (owner == Guid.Empty) throw new AlbumReviewReadException(401, "unauthenticated", "A signed-in owner is required.");
        if (limit is < 1 or > 100 || offset is < 0 or > 10000 || minRemaining < 0 || maxRemaining < minRemaining
            || (from?.Length ?? 0) > 4096 || (query?.Length ?? 0) > 256)
            throw BadRequest("Invalid search bounds: limit 1..100, offset 0..10000, remaining >= 0, from <= 4096, query <= 256.");
        var state = -1;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<EnrichmentStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed))
                throw BadRequest("Unknown enrichment status.");
            state = (int)parsed;
        }
        var fromText = from ?? "";
        var queryText = query ?? "";
        // Aggregate in PostgreSQL; never materialize/export the library to group it in memory.
        // Filters on names/status choose groups, without removing their other members/counts.
        var rows = await db.Database.SqlQuery<AlbumReviewGroup>($"""
            WITH members AS (
                SELECT *, COALESCE(NULLIF(regexp_replace("SourcePath", '/[^/]*$', ''), ''), '/') AS parent
                FROM "Songs" WHERE "OwnerUserId" = {owner} AND "DeletedAtUtc" IS NULL
            )
            SELECT parent AS "From", min("Id") AS "AnchorSongId", count(*)::int AS "TrackCount",
                count(*) FILTER (WHERE "EnrichmentStatus" <> 1)::int AS "RemainingCount",
                count(*) FILTER (WHERE "EnrichmentStatus" = 1)::int AS "MatchedCount",
                min("Album") AS "Album", min("AlbumArtist") AS "AlbumArtist",
                count(DISTINCT "Album")::int AS "AlbumNameCount"
            FROM members
            GROUP BY parent
            HAVING strpos(lower(parent), lower({fromText})) > 0
                AND bool_or(strpos(lower(concat_ws(' ', "Artist", "AlbumArtist", "Album", "Title",
                    "OriginalArtist", "OriginalAlbumArtist", "OriginalAlbum", "OriginalTitle")), lower({queryText})) > 0)
                AND ({state} = -1 OR bool_or("EnrichmentStatus" = {state}))
                AND count(*) FILTER (WHERE "EnrichmentStatus" <> 1) BETWEEN {minRemaining} AND {maxRemaining}
            ORDER BY (count(*) FILTER (WHERE "EnrichmentStatus" <> 1) = 0), "RemainingCount", parent COLLATE "C"
            OFFSET {offset} LIMIT {limit + 1}
            """).ToListAsync(ct);
        var more = rows.Count > limit;
        return new(rows.Take(limit).ToArray(), offset, limit, more ? offset + limit : null,
            "remaining-positive-first,remaining-ascending,from-ordinal", "EnrichmentStatus != Matched; not musical completeness");
    }

    internal static async Task<AlbumReviewContext> ReadAsync(
        MusicHoarderDbContext db, IFileSystem files, Guid owner, AlbumReviewContextRequest request, CancellationToken ct)
    {
        if (owner == Guid.Empty) throw new AlbumReviewReadException(401, "unauthenticated", "A signed-in owner is required.");
        if (request.Froms is null || request.Froms.Length is < 1 or > 16
            || request.Froms.Any(p => string.IsNullOrEmpty(p) || p.Length > 4096 || !p.StartsWith('/')
                || (p.Length > 1 && p.EndsWith('/')) || p.Contains('\0'))
            || (request.TargetSongIds is not null && (request.TargetSongIds.Length is < 1 or > MaxTracks
                || request.TargetSongIds.Any(id => id <= 0))))
            throw BadRequest("Supply 1..16 exact absolute POSIX parents (no trailing slash), and optionally 1..200 positive target IDs.");
        var froms = request.Froms.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var owned = db.Songs.AsNoTracking().Where(s => s.OwnerUserId == owner && s.DeletedAtUtc == null);
        var members = new Dictionary<int, SongMetadata>();
        foreach (var parent in froms)
        {
            var prefix = parent == "/" ? "/" : parent + "/";
            var rows = await Project(owned.Where(s => s.SourcePath.StartsWith(prefix)
                && !s.SourcePath.Substring(prefix.Length).Contains("/"))).Take(MaxTracks + 1).ToListAsync(ct);
            if (rows.Count == 0) throw NotFound();
            foreach (var song in rows) members[song.Id] = song;
            CheckTrackLimit(members.Count);
        }
        var targetIds = (request.TargetSongIds ?? members.Keys.ToArray()).Distinct().Order().ToArray();
        if (targetIds.Any(id => !members.ContainsKey(id))) throw NotFound();
        var reasons = members.Keys.ToDictionary(id => id, id => new SortedSet<string>(StringComparer.Ordinal)
            { targetIds.Contains(id) ? "selected" : "sameFrom" });

        // Expand exact metadata relationships only. These are contextual relationships, never
        // an assertion of edition/program identity. No prefix/fuzzy sibling discovery.
        foreach (var seed in members.Values.GroupBy(s => new { Artist = s.AlbumArtist ?? s.Artist,
            s.Album, s.MusicBrainzReleaseId, Destination = Parent(s.DestinationPath) }).Select(g => g.First()).ToArray())
        {
            var artist = seed.AlbumArtist ?? seed.Artist;
            var release = seed.MusicBrainzReleaseId;
            var destination = Parent(seed.DestinationPath);
            var destinationPrefix = destination is null ? null : destination == "/" ? "/" : destination + "/";
            var neighbors = await Project(owned.Where(s =>
                (release != null && release != "" && s.MusicBrainzReleaseId == release)
                || (artist != null && artist != "" && seed.Album != null && seed.Album != ""
                    && (s.AlbumArtist ?? s.Artist) == artist && s.Album == seed.Album)
                || (destinationPrefix != null && s.DestinationPath != null && s.DestinationPath.StartsWith(destinationPrefix)
                    && !s.DestinationPath.Substring(destinationPrefix.Length).Contains("/"))))
                .Take(MaxTracks + 1).ToListAsync(ct);
            foreach (var song in neighbors)
            {
                Add(song, "relatedMetadataOrDestination");
            }
        }

        // Close the duplicate graph (active AND dismissed), plus legacy keeper pointers.
        // Every reference endpoint must resolve to a live row of this owner before inclusion.
        List<SongDuplicateLink> links;
        while (true)
        {
            var ids = members.Keys.ToArray();
            links = await db.SongDuplicateLinks.AsNoTracking()
                .Where(l => l.OwnerUserId == owner && (ids.Contains(l.SongIdLow) || ids.Contains(l.SongIdHigh)))
                .Where(l => owned.Any(s => s.Id == l.SongIdLow) && owned.Any(s => s.Id == l.SongIdHigh))
                .OrderBy(l => l.Id).Take(MaxEvidenceRows + 1).ToListAsync(ct);
            CheckEvidenceLimit(links.Count);
            var referenced = links.SelectMany(l => new[] { l.SongIdLow, l.SongIdHigh })
                .Concat(members.Values.Where(s => s.DuplicateOfId.HasValue).Select(s => s.DuplicateOfId!.Value)).Distinct().ToArray();
            var more = await Project(owned.Where(s => referenced.Contains(s.Id)
                || (s.DuplicateOfId.HasValue && ids.Contains(s.DuplicateOfId.Value))))
                .Take(MaxTracks + 1).ToListAsync(ct);
            var before = members.Count;
            foreach (var song in more) Add(song, "nativeDuplicate");
            if (members.Count == before) break;
        }

        var allIds = members.Keys.Order().ToArray();
        var changes = await db.SongMetadataChanges.AsNoTracking()
            .Where(c => allIds.Contains(c.SongId) && c.Song.OwnerUserId == owner && TagFields.Contains(c.FieldName))
            .OrderBy(c => c.Id).Take(MaxEvidenceRows + 1).ToListAsync(ct);
        var attempts = await db.SongProviderAttempts.AsNoTracking()
            .Where(a => allIds.Contains(a.SongId) && a.Song.OwnerUserId == owner)
            .OrderBy(a => a.Id).Take(MaxEvidenceRows + 1).ToListAsync(ct);
        CheckEvidenceLimit(links.Count + changes.Count + attempts.Count);
        var tracks = members.Values.OrderBy(s => s.Id).Select(s => new AlbumReviewTrack(
            s.Id, Parent(s.SourcePath)!, s.SourcePath, s.FileName, reasons[s.Id].ToArray(),
            s.FileSizeBytes, s.LastModifiedUtc, s.MatchedBy, s.MatchConfidence, s.EnrichmentLastAttemptedAtUtc,
            s.EnrichmentStatus.ToString(),
            s.IndexedAtUtc, s.EnrichedAtUtc, s.DurationMs, s.DurationSeconds,
            new(s.OriginalMetadataCaptured ? "capturedOriginalTags" : "notCaptured",
                s.OriginalMetadataCapturedAtUtc, s.OriginalMetadataCaptured ? Values(s, original: true) : new Dictionary<string, object?>()),
            new("currentDatabaseTags", null, Values(s)), s.IsManuallyApproved, s.ManuallyApprovedAtUtc,
            changes.Where(c => c.SongId == s.Id && s.IsManuallyApproved && s.ManuallyApprovedAtUtc.HasValue
                && c.Source == ManualReviewMetadata.Source && c.AppliedAtUtc == s.ManuallyApprovedAtUtc && c.RevertedAtUtc == null)
                .GroupBy(c => c.FieldName, StringComparer.Ordinal).Select(g => g.MaxBy(c => c.Id)!)
                .OrderBy(c => c.FieldName, StringComparer.Ordinal)
                .Select(c => new AlbumReviewManualField(c.FieldName, c.NewValue, c.NewValue is null ? "explicitUnknown" : "approvedValue", c.Id)).ToArray(),
            changes.Where(c => c.SongId == s.Id).Select(c => new AlbumReviewChange(c.Id, c.FieldName, c.OldValue,
                c.NewValue, c.Source, c.Confidence, c.CreatedAtUtc, c.AppliedAtUtc, c.RevertedAtUtc)).ToArray(),
            attempts.Where(a => a.SongId == s.Id).Select(Candidate).ToArray(), Warnings(s.MatchWarnings),
            s.EnrichmentError != null, s.IsDuplicate, s.DuplicateOfId is { } keeper && members.ContainsKey(keeper) ? keeper : null,
            ObserveCopy(files, s))).ToArray();

        var programs = await ProgramsAsync(db, members.Values.ToArray(), ct);
        CheckEvidenceLimit(links.Count + changes.Count + attempts.Count + programs.Sum(p => p.Tracks.Count + p.Sources.Count + 1));
        var conflicts = Conflicts(tracks);
        var packet = new AlbumReviewContext("album-review-context-v1", "", default,
            new(request.TargetSongIds is null ? "exactFromMembers" : "explicitTracks", froms, targetIds, false, "notEstablished"),
            tracks, links.Select(l => new AlbumReviewDuplicate(l.Id, l.SongIdLow, l.SongIdHigh,
                l.Status.ToString(), l.Confidence.ToString(), l.Reasons.ToString(), l.Similarity, l.DetectedAtUtc, l.DismissedAtUtc)).ToArray(),
            conflicts, programs, true, MaxTracks, MaxEvidenceRows, MaxResponseBytes);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
        if (bytes.Length > MaxResponseBytes - 256)
            throw new AlbumReviewReadException(413, "response_limit", "Evidence exceeds the response byte budget; narrow the exact selection. No partial packet returned.", MaxResponseBytes);
        // Bind the observation to its owner without exposing the owner identifier in the DTO.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(owner.ToByteArray());
        hash.AppendData(bytes);
        var revision = Convert.ToHexStringLower(hash.GetHashAndReset());
        return packet with { Revision = revision, ObservedAtUtc = DateTime.UtcNow };

        void Add(SongMetadata song, string reason)
        {
            members.TryAdd(song.Id, song);
            if (!reasons.TryGetValue(song.Id, out var set)) reasons[song.Id] = set = new(StringComparer.Ordinal);
            set.Add(reason);
            CheckTrackLimit(members.Count);
        }
    }

    internal static string? Parent(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : slash == 0 ? "/" : path[..slash];
    }

    private static Dictionary<string, object?> Values(object value, bool original = false) =>
        (original ? OriginalFields : TagFields).ToDictionary(f => f,
            f => value.GetType().GetProperty((original ? "Original" : "") + f)?.GetValue(value), StringComparer.Ordinal);

    private static AlbumReviewCandidate Candidate(SongProviderAttempt attempt)
    {
        EnrichmentProviderResult? candidate = null;
        var state = "absent";
        if (!string.IsNullOrWhiteSpace(attempt.MatchedDataJson))
        {
            try
            {
                candidate = JsonSerializer.Deserialize<EnrichmentProviderResult>(attempt.MatchedDataJson, ProviderJson);
                state = candidate is null ? "invalid" : "available";
            }
            catch (JsonException) { state = "invalid"; }
        }
        return new(attempt.Id, attempt.Provider.ToString(), attempt.Status.ToString(), attempt.AttemptedAtUtc,
            attempt.RetryAfterUtc, attempt.Error != null, state,
            candidate is null ? null : new("storedProviderCandidate", attempt.AttemptedAtUtc, Values(candidate)),
            candidate?.DurationSeconds, candidate?.MatchConfidence, candidate?.MatchWarnings ?? []);
    }

    private static IReadOnlyList<string> Warnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? ["invalidStoredWarnings"]; }
        catch (JsonException) { return ["invalidStoredWarnings"]; }
    }

    private static AlbumReviewCopy ObserveCopy(IFileSystem files, SongMetadata song)
    {
        var current = ObserveFile(files, song.DestinationPath);
        var previous = string.IsNullOrEmpty(song.PreviousDestinationPath) ? null : ObserveFile(files, song.PreviousDestinationPath);
        return new(song.DestinationPath, current.Presence, current.SizeBytes, current.LastModifiedUtc,
            song.LibraryBuildStatus.ToString(), song.LibraryBuildAttempts, song.LibraryBuiltAtUtc,
            song.LibraryBuildLastAttemptedAtUtc, song.LibraryBuildError != null, "unknown", previous);
    }

    private static AlbumReviewFileObservation ObserveFile(IFileSystem files, string? path)
    {
        var presence = "notConfigured";
        long? size = null;
        DateTime? modified = null;
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                // GetAttributes throws on inaccessible paths; File.Exists would hide that as false.
                var attributes = files.File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0) presence = "notAFile";
                else
                {
                    var info = files.FileInfo.New(path);
                    size = info.Length;
                    modified = info.LastWriteTimeUtc;
                    presence = "present";
                }
            }
            catch (FileNotFoundException) { presence = "missing"; }
            catch (DirectoryNotFoundException) { presence = "missing"; }
            catch (IOException) { presence = "unknown"; }
            catch (UnauthorizedAccessException) { presence = "unknown"; }
            catch (ArgumentException) { presence = "unknown"; }
            catch (NotSupportedException) { presence = "unknown"; }
        }
        return new(path ?? "", presence, size, modified);
    }

    private static IReadOnlyList<AlbumReviewConflict> Conflicts(IReadOnlyList<AlbumReviewTrack> tracks)
    {
        var conflicts = new List<AlbumReviewConflict>();
        foreach (var group in tracks.Where(t => !string.IsNullOrEmpty(t.Copy.Path)).GroupBy(t => t.Copy.Path!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            conflicts.Add(new("destinationPath", group.Key, group.Select(t => t.Id).Order().ToArray()));
        // A conflicting/unknown edition must not hide a possible slot collision. Differing
        // editions/years qualify the warning; they are never merged. Unknown disc stays unknown.
        foreach (var group in tracks.Where(t => t.Current.Values["TrackNumber"] is int)
            .GroupBy(t => new { Artist = t.Current.Values["AlbumArtist"] ?? t.Current.Values["Artist"],
                Album = t.Current.Values["Album"],
                Disc = t.Current.Values["DiscNumber"], Track = t.Current.Values["TrackNumber"] })
            .Where(g => g.Key.Album is not null && g.Count() > 1))
        {
            var uncertain = group.Key.Disc is null
                || group.Select(t => (t.Current.Values["Year"], t.Current.Values["MusicBrainzReleaseId"])).Distinct().Count() > 1;
            conflicts.Add(new(uncertain ? "possibleAlbumPosition" : "albumPosition", JsonSerializer.Serialize(group.Key, Json), group.Select(t => t.Id).Order().ToArray()));
        }
        return conflicts;
    }

    private static async Task<IReadOnlyList<AlbumReviewProgram>> ProgramsAsync(
        MusicHoarderDbContext db, IReadOnlyList<SongMetadata> songs, CancellationToken ct)
    {
        var keys = songs.Select(s => new { Song = s, Artist = TitleNormalizer.NormalizeForSearch(s.AlbumArtist ?? s.Artist),
            Album = TitleNormalizer.NormalizeForSearch(s.Album) }).Where(k => k.Artist.Length > 0 && k.Album.Length > 0).ToArray();
        var result = new List<AlbumReviewProgram>();
        foreach (var group in keys.GroupBy(k => new { k.Artist, k.Album }).OrderBy(g => g.Key.Artist, StringComparer.Ordinal).ThenBy(g => g.Key.Album, StringComparer.Ordinal))
        {
            var album = await db.CanonicalAlbums.AsNoTracking()
                .SingleOrDefaultAsync(a => a.ArtistKey == group.Key.Artist && a.AlbumKey == group.Key.Album, ct);
            if (album is null) continue;
            var tracks = await db.CanonicalAlbumTracks.AsNoTracking().Where(t => t.CanonicalAlbumId == album.Id)
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Id)
                .Select(t => new AlbumReviewProgramTrack(t.DiscNumber, t.TrackNumber, t.Title, t.DurationMs,
                    t.MusicBrainzRecordingId, t.CorroboratingProviders, t.IsContested))
                .Take(MaxEvidenceRows + 1).ToListAsync(ct);
            CheckEvidenceLimit(tracks.Count + result.Sum(p => p.Tracks.Count + 1));
            var (state, sources) = ProgramSources(album.SourcesJson);
            result.Add(new(album.Id, group.Select(k => k.Song.Id).Order().ToArray(), "normalizedNameCacheOnly",
                album.DisplayArtist, album.DisplayTitle, album.Year, album.Status.ToString(), album.FetchedAtUtc,
                album.ResolvedTrackCount, album.TrackCountContested, state, sources, tracks));
        }
        return result;
    }

    private static (string State, IReadOnlyList<AlbumReviewProgramSource> Sources) ProgramSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ("absent", []);
        try
        {
            var sources = JsonSerializer.Deserialize<AlbumTracklistReconciler.ReconciledSource[]>(json, ProviderJson);
            return sources is null ? ("invalid", []) : ("available", sources.Select(s =>
                new AlbumReviewProgramSource(s.Provider.ToString(), s.AlbumId, s.TrackCount, s.InWinningCluster)).ToArray());
        }
        catch (JsonException) { return ("invalid", []); }
    }

    private static void CheckTrackLimit(int count)
    {
        if (count > MaxTracks) throw new AlbumReviewReadException(413, "track_limit",
            "Selection and related context exceed the track budget; use fewer exact parents. No evidence was silently truncated.", MaxTracks);
    }
    private static void CheckEvidenceLimit(int count)
    {
        if (count > MaxEvidenceRows) throw new AlbumReviewReadException(413, "evidence_limit",
            "Stored evidence exceeds the row budget. No partial packet returned.", MaxEvidenceRows);
    }
    private static AlbumReviewReadException BadRequest(string message) => new(400, "invalid_selection", message);
    private static AlbumReviewReadException NotFound() => new(404, "selection_not_found", "The complete selection is unavailable to this owner.");

    // Projection generated from the explicit tag allowlist and provenance fields below.
    private static IQueryable<SongMetadata> Project(IQueryable<SongMetadata> query) => query.Select(s => new SongMetadata
    {
        Id = s.Id,
        OwnerUserId = s.OwnerUserId,
        SourcePath = s.SourcePath,
        FileSizeBytes = s.FileSizeBytes,
        FileName = s.FileName,
        Extension = s.Extension,
        LastModifiedUtc = s.LastModifiedUtc,
        IndexedAtUtc = s.IndexedAtUtc,
        EnrichmentStatus = s.EnrichmentStatus,
        EnrichedAtUtc = s.EnrichedAtUtc,
        EnrichmentLastAttemptedAtUtc = s.EnrichmentLastAttemptedAtUtc,
        MatchedBy = s.MatchedBy,
        MatchConfidence = s.MatchConfidence,
        MatchWarnings = s.MatchWarnings,
        EnrichmentError = s.EnrichmentError,
        IsManuallyApproved = s.IsManuallyApproved,
        ManuallyApprovedAtUtc = s.ManuallyApprovedAtUtc,
        OriginalMetadataCaptured = s.OriginalMetadataCaptured,
        OriginalMetadataCapturedAtUtc = s.OriginalMetadataCapturedAtUtc,
        DurationMs = s.DurationMs,
        DurationSeconds = s.DurationSeconds,
        IsDuplicate = s.IsDuplicate,
        DuplicateOfId = s.DuplicateOfId,
        DestinationPath = s.DestinationPath,
        PreviousDestinationPath = s.PreviousDestinationPath,
        LibraryBuildStatus = s.LibraryBuildStatus,
        LibraryBuildAttempts = s.LibraryBuildAttempts,
        LibraryBuiltAtUtc = s.LibraryBuiltAtUtc,
        LibraryBuildLastAttemptedAtUtc = s.LibraryBuildLastAttemptedAtUtc,
        LibraryBuildError = s.LibraryBuildError,
        Title = s.Title,
        Artist = s.Artist,
        Artists = s.Artists,
        AlbumArtist = s.AlbumArtist,
        Album = s.Album,
        Year = s.Year,
        TrackNumber = s.TrackNumber,
        DiscNumber = s.DiscNumber,
        TotalDiscs = s.TotalDiscs,
        TotalTracks = s.TotalTracks,
        IsCompilation = s.IsCompilation,
        ReleaseTypePrimary = s.ReleaseTypePrimary,
        ReleaseTypes = s.ReleaseTypes,
        MusicBrainzId = s.MusicBrainzId,
        MusicBrainzReleaseId = s.MusicBrainzReleaseId,
        MusicBrainzReleaseGroupId = s.MusicBrainzReleaseGroupId,
        ArtistMusicBrainzIds = s.ArtistMusicBrainzIds,
        AlbumArtistMusicBrainzId = s.AlbumArtistMusicBrainzId,
        SpotifyId = s.SpotifyId,
        AcoustIdTrackId = s.AcoustIdTrackId,
        Isrc = s.Isrc,
        Genre = s.Genre,
        ReleaseDate = s.ReleaseDate,
        OriginalReleaseDate = s.OriginalReleaseDate,
        Label = s.Label,
        CatalogNumber = s.CatalogNumber,
        Upc = s.Upc,
        Composer = s.Composer,
        Copyright = s.Copyright,
        ArtistSort = s.ArtistSort,
        AlbumArtistSort = s.AlbumArtistSort,
        OriginalTitle = s.OriginalTitle,
        OriginalArtist = s.OriginalArtist,
        OriginalArtists = s.OriginalArtists,
        OriginalAlbumArtist = s.OriginalAlbumArtist,
        OriginalAlbum = s.OriginalAlbum,
        OriginalYear = s.OriginalYear,
        OriginalTrackNumber = s.OriginalTrackNumber,
        OriginalDiscNumber = s.OriginalDiscNumber,
        OriginalTotalDiscs = s.OriginalTotalDiscs,
        OriginalTotalTracks = s.OriginalTotalTracks,
        OriginalIsCompilation = s.OriginalIsCompilation,
        OriginalReleaseTypePrimary = s.OriginalReleaseTypePrimary,
        OriginalReleaseTypes = s.OriginalReleaseTypes,
        OriginalMusicBrainzId = s.OriginalMusicBrainzId,
        OriginalSpotifyId = s.OriginalSpotifyId,
        OriginalIsrc = s.OriginalIsrc,
    });
}
