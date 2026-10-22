using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;
using Npgsql;

namespace MusicHoarder.Api.Review;

/// <summary>SQL inventory and bounded evidence, with no musical identity inference or provider access.</summary>
internal static class AlbumAuditReader
{
    internal const string Version = "album-audit-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions RevisionJson = CreateRevisionJson();
    private static JsonSerializerOptions CreateRevisionJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new PersistableDate());
        return options;
    }
    private sealed class PersistableDate : System.Text.Json.Serialization.JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDateTime();
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue(ReviewTimestamp.Persisted(value));
    }
    private sealed record Cursor(string Version, string Binding, string Revision, string Key, int LastId);
    internal sealed record InventoryState(string Revision, long Tracks, long Groups);
    internal sealed record GroupRow(string Key, string Kind, int Count, int[] Ids, string? Artist, string? Album,
        int FromCount, int DestinationCount, int Built, int NoDestination, string StatusJson, string Revision, int AlbumCount, int ArtistCount);
    internal sealed record RelationRow(string Key, string CandidateKey, int Count);
    internal sealed record RelatedRow(int Id, bool Blocking, string[] Reasons);
    internal sealed record RelatedState(int Count, int BlockingCount, string Revision);
    internal sealed record AuditRead(AlbumAuditContext Packet, AlbumReviewContext? PlanPacket);

    // Destination grouping follows the unmerged library folder projection. Unbuilt rows are
    // deliberately grouped by exact From, not by normalized names. Invalid/empty parents stay separate.
    private const string InventorySql = """
        WITH base AS (
          SELECT "Id", "Album", coalesce("AlbumArtist", "Artist") AS artist,
            "EnrichmentStatus", "LibraryBuildStatus", "DestinationPath", "SourcePath", "Title",
            "OriginalTitle", "OriginalArtist", "OriginalAlbumArtist", "OriginalAlbum",
            CASE WHEN strpos("SourcePath", '/') > 0 THEN regexp_replace("SourcePath", '/[^/]*$', '') ELSE NULL END AS src,
            CASE WHEN nullif(btrim("DestinationPath"), '') IS NOT NULL THEN
              CASE WHEN strpos(reverse(btrim("DestinationPath")), '/') > 0
                   AND length(btrim("DestinationPath")) - strpos(reverse(btrim("DestinationPath")), '/') > 0
                   THEN regexp_replace(btrim("DestinationPath"), '/[^/]*$', '') ELSE btrim("DestinationPath") END
              ELSE NULL END AS dest
          FROM "Songs" WHERE "OwnerUserId" = @owner AND "DeletedAtUtc" IS NULL
        ), units AS (
          SELECT *, CASE WHEN dest IS NOT NULL THEN 'v1:d:' || dest
            WHEN src IS NOT NULL THEN 'v1:f:' || coalesce(nullif(src, ''), '/')
            ELSE 'v1:u:' || "Id"::text END AS key,
            CASE WHEN dest IS NOT NULL THEN 'destination' WHEN src IS NOT NULL THEN 'from' ELSE 'unassigned' END AS kind
          FROM base
        )
        """;

    internal static async Task<AlbumAuditInventory> InventoryAsync(MusicHoarderDbContext db, Guid owner,
        string? query, string? status, int limit, string? cursor, CancellationToken ct, AlbumAuditCursorCodec? codec = null)
    {
        CheckOwner(owner);
        if (limit is < 1 or > 100 || (query?.Length ?? 0) > 256) throw Bad("Use limit 1..100 and query <= 256 characters.");
        var state = ParseStatus(status);
        var binding = Hash(new { owner, query = query ?? "", state });
        var prior = Decode(cursor, binding, codec);
        var inventory = await InventoryStateAsync(db, owner, ct);
        CheckRevision(prior, inventory.Revision, "inventory_changed");
        var rows = await db.Database.SqlQueryRaw<GroupRow>(InventorySql + """
            , ranked AS (
              SELECT *, row_number() OVER (PARTITION BY key ORDER BY "Id") rn,
                (count(*) OVER (PARTITION BY key, "EnrichmentStatus"))::int state_count
              FROM units WHERE key COLLATE "C" > @last COLLATE "C"
            )
            SELECT key AS "Key", min(kind) AS "Kind", count(*)::int AS "Count",
              array_agg("Id" ORDER BY "Id") FILTER (WHERE rn <= 200) AS "Ids",
              min(artist) AS "Artist", min("Album") AS "Album", count(DISTINCT src)::int AS "FromCount",
              count(DISTINCT dest)::int AS "DestinationCount", count(DISTINCT "Album")::int AS "AlbumCount",
              count(DISTINCT artist)::int AS "ArtistCount", count(*) FILTER (WHERE "LibraryBuildStatus" = 3)::int AS "Built",
              count(*) FILTER (WHERE dest IS NULL)::int AS "NoDestination",
              jsonb_object_agg("EnrichmentStatus"::text, state_count)::text AS "StatusJson",
              md5(string_agg(md5(row("Id", "SourcePath", "DestinationPath", artist, "Album", "EnrichmentStatus", "LibraryBuildStatus")::text), '' ORDER BY "Id")) AS "Revision"
            FROM ranked GROUP BY key
            HAVING bool_or(strpos(lower(concat_ws(' ', key, artist, "Album", "Title", "OriginalTitle", "OriginalArtist", "OriginalAlbumArtist", "OriginalAlbum")), lower(@query)) > 0)
              AND (@state = -1 OR bool_or("EnrichmentStatus" = @state))
            ORDER BY key COLLATE "C" LIMIT @limit
            """, P("owner", owner), P("last", prior?.Key ?? ""), P("query", query ?? ""), P("state", state), P("limit", limit + 1)).ToListAsync(ct);
        // One bounded projection query for this page, not one detail query per track/group.
        // These are label-based navigation hints only. Complete dependency discovery is in context.
        var seedsJson = JsonSerializer.Serialize(rows.Take(limit).Select(r => new { key = r.Key, artist = r.Artist, album = r.Album }), Json);
        var relations = await db.Database.SqlQueryRaw<RelationRow>(InventorySql + """
            , seeds AS (SELECT * FROM jsonb_to_recordset(@seeds::jsonb) AS s(key text, artist text, album text)),
            identities AS (SELECT DISTINCT key, artist, "Album" AS album FROM units
              WHERE nullif(artist, '') IS NOT NULL AND nullif("Album", '') IS NOT NULL),
            catalogs AS (SELECT artist, album, array_agg(key ORDER BY key COLLATE "C") keys FROM identities GROUP BY artist, album)
            SELECT s.key AS "Key", candidate AS "CandidateKey", (cardinality(c.keys) - CASE WHEN s.key = ANY(c.keys) THEN 1 ELSE 0 END)::int AS "Count"
            FROM seeds s JOIN catalogs c ON c.artist = s.artist AND c.album = s.album
            CROSS JOIN LATERAL (SELECT k AS candidate FROM unnest(c.keys[1:9]) k WHERE k <> s.key LIMIT 8) candidates
            ORDER BY s.key COLLATE "C", candidate COLLATE "C"
            """, P("owner", owner), P("seeds", seedsJson)).ToListAsync(ct);
        var groups = rows.Take(limit).Select(r => new AlbumAuditGroup(r.Key, r.Kind, r.Count, r.Ids,
            r.Count <= r.Ids.Length, r.Count <= r.Ids.Length ? null : Encode(new(Version, Hash(new { owner, members = true }), inventory.Revision, r.Key, r.Ids[^1]), codec),
            r.Artist, r.Album, r.FromCount, r.DestinationCount, r.Built, r.NoDestination,
            JsonSerializer.Deserialize<Dictionary<string, int>>(r.StatusJson)!.ToDictionary(p => ((EnrichmentStatus)int.Parse(p.Key)).ToString(), p => p.Value),
            r.Revision, new[] { r.FromCount > 1 ? "multipleExactFroms" : null, r.AlbumCount > 1 ? "multipleAlbumLabels" : null,
                r.ArtistCount > 1 ? "multipleAlbumArtistLabels" : null }.OfType<string>().ToArray(),
            relations.Where(c => c.Key == r.Key).Select(c => new AlbumAuditRelationCandidate(c.CandidateKey, "exactDisplayCreditAndAlbumNavigationHint")).ToArray(),
            relations.FirstOrDefault(c => c.Key == r.Key)?.Count ?? 0,
            (relations.FirstOrDefault(c => c.Key == r.Key)?.Count ?? 0) <= 8,
            "/api/review/album-audit/context: select explicit memberSongIds; members are not auto-merged")).ToArray();
        var result = new AlbumAuditInventory(Version, inventory.Revision, DateTime.UtcNow, groups,
            rows.Count > limit ? Encode(new(Version, binding, inventory.Revision, rows[limit - 1].Key, 0), codec) : null,
            new(inventory.Tracks, inventory.Groups, "Every active owned track in exactly one unmerged primary unit; deleted/shared rows excluded."));
        if (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > AlbumReviewContextReader.MaxResponseBytes)
            throw new AlbumReviewReadException(413, "evidence_too_large", "Inventory page exceeds the response byte budget; reduce limit. No partial page returned.");
        return result;
    }

    internal static async Task<AlbumAuditMemberPage> MembersAsync(MusicHoarderDbContext db, Guid owner, string cursor, CancellationToken ct, AlbumAuditCursorCodec? codec = null)
    {
        CheckOwner(owner);
        var prior = Decode(cursor, Hash(new { owner, members = true }), codec) ?? throw Bad("A member cursor is required.");
        var state = await InventoryStateAsync(db, owner, ct);
        CheckRevision(prior, state.Revision, "inventory_changed");
        var ids = await db.Database.SqlQueryRaw<int>(InventorySql + """
            SELECT "Id" AS "Value" FROM units WHERE key = @key AND "Id" > @last ORDER BY "Id" LIMIT 201
            """, P("owner", owner), P("key", prior.Key), P("last", prior.LastId)).ToArrayAsync(ct);
        return new(ids.Take(200).ToArray(), ids.Length > 200 ? Encode(prior with { LastId = ids[199] }, codec) : null, state.Revision);
    }

    private static async Task<InventoryState> InventoryStateAsync(MusicHoarderDbContext db, Guid owner, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<InventoryState>(InventorySql + """
            SELECT md5(coalesce(string_agg(md5(row("Id", key, "SourcePath", "DestinationPath", artist, "Album", "Title", "OriginalTitle", "OriginalArtist", "OriginalAlbumArtist", "OriginalAlbum", "EnrichmentStatus", "LibraryBuildStatus")::text), '' ORDER BY "Id"), '')) AS "Revision",
              count(*) AS "Tracks", count(DISTINCT key) AS "Groups" FROM units
            """, P("owner", owner)).ToListAsync(ct)).Single();

    // Duplicate edges are closed transitively, including dismissed links and legacy pointers.
    // Other relationships are exact, one hop from the explicitly chosen members.
    private const string RelatedSql = """
        WITH RECURSIVE owned AS (
          SELECT "Id", "SourcePath", "DestinationPath", "AlbumArtist", "Artist", "Album", "MusicBrainzReleaseId",
            "DuplicateOfId", "IsDuplicate", "EnrichmentStatus", "TrackNumber", "DiscNumber", "TotalTracks", "TotalDiscs",
            "Year", "Title", "DurationMs", "DurationSeconds", "IsManuallyApproved", "ManuallyApprovedAtUtc",
            "FileSizeBytes", "LastModifiedUtc", "LibraryBuildStatus", "LibraryBuiltAtUtc", "ReviewPlanId",
            "Artists", "IsCompilation", "ReleaseTypePrimary", "ReleaseTypes", "MusicBrainzId",
            "MusicBrainzReleaseGroupId", "ArtistMusicBrainzIds", "AlbumArtistMusicBrainzId", "SpotifyId", "AcoustIdTrackId", "Isrc",
            "Genre", "ReleaseDate", "OriginalReleaseDate", "Label", "CatalogNumber", "Upc", "Composer", "Copyright", "ArtistSort", "AlbumArtistSort",
            "OriginalMetadataCaptured", "OriginalMetadataCapturedAtUtc", "OriginalTitle", "OriginalArtist", "OriginalArtists", "OriginalAlbumArtist",
            "OriginalAlbum", "OriginalYear", "OriginalTrackNumber", "OriginalDiscNumber", "OriginalTotalDiscs", "OriginalTotalTracks", "OriginalIsCompilation",
            "OriginalReleaseTypePrimary", "OriginalReleaseTypes", "OriginalMusicBrainzId", "OriginalSpotifyId", "OriginalIsrc",
            "EnrichedAtUtc", "EnrichmentLastAttemptedAtUtc", "MatchedBy", "MatchConfidence", "MatchWarnings", ("EnrichmentError" IS NOT NULL) AS "HasEnrichmentError"
          FROM "Songs" WHERE "OwnerUserId" = @owner AND "DeletedAtUtc" IS NULL
        ), seeds AS (SELECT * FROM owned WHERE "Id" = ANY(@ids)),
        edge0 AS (
          SELECT "SongIdLow" a, "SongIdHigh" b FROM "SongDuplicateLinks" WHERE "OwnerUserId" = @owner
          UNION SELECT "Id", "DuplicateOfId" FROM owned WHERE "DuplicateOfId" IS NOT NULL
        ), edges AS (SELECT a, b FROM edge0 UNION SELECT b, a FROM edge0),
        walk(id) AS (SELECT "Id" FROM seeds UNION SELECT e.b FROM walk w JOIN edges e ON e.a = w.id JOIN owned o ON o."Id" = e.b),
        related AS (
          SELECT o.*, array_remove(ARRAY[
            CASE WHEN EXISTS(SELECT 1 FROM seeds s WHERE strpos(s."SourcePath", '/') > 0 AND
              regexp_replace(o."SourcePath", '/[^/]*$', '') = regexp_replace(s."SourcePath", '/[^/]*$', '')) THEN 'sameFrom' END,
            CASE WHEN EXISTS(SELECT 1 FROM seeds s WHERE nullif(s."DestinationPath", '') IS NOT NULL AND
              regexp_replace(o."DestinationPath", '/[^/]*$', '') = regexp_replace(s."DestinationPath", '/[^/]*$', '')) THEN 'sameDestination' END,
            CASE WHEN EXISTS(SELECT 1 FROM seeds s WHERE nullif(s."Album", '') IS NOT NULL AND nullif(coalesce(s."AlbumArtist", s."Artist"), '') IS NOT NULL AND
              o."Album" = s."Album" AND coalesce(o."AlbumArtist", o."Artist") = coalesce(s."AlbumArtist", s."Artist")) THEN 'exactCreditAndAlbum' END,
            CASE WHEN EXISTS(SELECT 1 FROM seeds s WHERE nullif(s."MusicBrainzReleaseId", '') IS NOT NULL AND o."MusicBrainzReleaseId" = s."MusicBrainzReleaseId") THEN 'sameReleaseId' END,
            CASE WHEN o."Id" IN (SELECT id FROM walk) THEN 'nativeDuplicate' END
          ]::text[], NULL) AS reasons
          FROM owned o WHERE NOT (o."Id" = ANY(@ids))
        ), neighbors AS (SELECT *, (reasons && ARRAY['sameDestination','exactCreditAndAlbum','sameReleaseId','nativeDuplicate']) AS blocking
          FROM related WHERE cardinality(reasons) > 0)
        """;

    internal static async Task<AuditRead> ReadAsync(MusicHoarderDbContext db, IFileSystem files, Guid owner,
        AlbumAuditContextRequest request, CancellationToken ct, AlbumAuditCursorCodec? codec = null)
    {
        CheckOwner(owner);
        var ids = request.MemberSongIds;
        if (ids is null || ids.Length is < 1 or > 200 || ids.Any(i => i <= 0) || ids.Distinct().Count() != ids.Length)
            throw new AlbumReviewReadException(ids?.Length > 200 ? 413 : 400, "scope_too_large", "Select 1..200 unique positive owned member IDs; a subset does not establish a complete program.", 200);
        if (request.Limit is < 1 or > 100) throw Bad("Related limit must be 1..100.");
        ids = ids.Order().ToArray();
        var owned = db.Songs.AsNoTracking().Where(s => s.OwnerUserId == owner && s.DeletedAtUtc == null);
        var selected = await AlbumReviewContextReader.Project(owned.Where(s => ids.Contains(s.Id))).ToDictionaryAsync(s => s.Id, ct);
        if (selected.Count != ids.Length) throw new AlbumReviewReadException(404, "selection_not_found", "The entire selection must be live and owned.");
        var stats = (await db.Database.SqlQueryRaw<RelatedState>(RelatedSql + """
            SELECT count(*)::int AS "Count", count(*) FILTER (WHERE blocking)::int AS "BlockingCount",
              md5(coalesce(string_agg(md5(row_to_json(n)::text) ||
                coalesce((SELECT md5(string_agg(md5(row_to_json(c)::text), '' ORDER BY c."Id")) FROM "SongMetadataChanges" c WHERE c."SongId" = n."Id"), '') ||
                coalesce((SELECT md5(string_agg(md5(row_to_json(a)::text), '' ORDER BY a."Id")) FROM "SongProviderAttempts" a WHERE a."SongId" = n."Id"), ''), '' ORDER BY "Id"), '')) AS "Revision" FROM neighbors n
            """, P("owner", owner), P("ids", ids)).ToListAsync(ct)).Single();
        var blockers = await db.Database.SqlQueryRaw<RelatedRow>(RelatedSql + """
            SELECT "Id", blocking AS "Blocking", reasons AS "Reasons" FROM neighbors WHERE blocking ORDER BY "Id" LIMIT 201
            """, P("owner", owner), P("ids", ids)).ToListAsync(ct);
        var ready = ids.Length + stats.BlockingCount <= 200;
        var evidenceIds = ready ? ids.Concat(blockers.Select(b => b.Id)).Order().ToArray() : ids;
        var evidenceSongs = ready && blockers.Count > 0
            ? await AlbumReviewContextReader.Project(owned.Where(s => evidenceIds.Contains(s.Id))).ToDictionaryAsync(s => s.Id, ct) : selected;
        var reasonMap = evidenceSongs.Keys.ToDictionary(i => i, i => new SortedSet<string>(ids.Contains(i) ? ["selected"] : blockers.Single(b => b.Id == i).Reasons, StringComparer.Ordinal));
        var links = await db.SongDuplicateLinks.AsNoTracking().Where(l => l.OwnerUserId == owner &&
            (evidenceIds.Contains(l.SongIdLow) || evidenceIds.Contains(l.SongIdHigh)) &&
            owned.Any(s => s.Id == l.SongIdLow) && owned.Any(s => s.Id == l.SongIdHigh)).OrderBy(l => l.Id).Take(AlbumReviewContextReader.MaxEvidenceRows + 1).ToListAsync(ct);
        if (links.Count > AlbumReviewContextReader.MaxEvidenceRows) throw TooLarge();
        var evidence = await EvidenceAsync(db, files, owner, evidenceSongs, reasonMap, ids,
            selected.Values.Select(s => AlbumReviewContextReader.Parent(s.SourcePath)!).Distinct().Order(StringComparer.Ordinal).ToArray(), links, ct, "boundedAlbumAuditV1");
        var references = evidence.Programs.Select(p => new AlbumAuditCachedReference(new("canonicalAlbum", p.Id, Hash(p with { RelatedSongIds = [] })), p)).ToArray();
        AlbumReviewProgram? reference = null;
        if (request.Reference is { } chosen)
        {
            if (chosen.Kind != "canonicalAlbum" || chosen.Id <= 0 || string.IsNullOrEmpty(chosen.Revision) || chosen.Revision.Length > 128) throw Bad("Use a typed cached canonicalAlbum reference.");
            var available = references.SingleOrDefault(r => r.Reference.Id == chosen.Id)
                ?? throw new AlbumReviewReadException(404, "reference_not_found", "Reference is not available through this owned selection.");
            if (available.Reference.Revision != chosen.Revision) throw new AlbumReviewReadException(409, "reference_changed", "Read and select the current cached reference explicitly.");
            reference = available.Program;
        }
        var membershipRevision = Hash(new { owner, version = Version, ids, request.Reference });
        var evidenceRevision = Hash(new { evidence = evidence with { Revision = "", ObservedAtUtc = default }, references });
        var revision = Hash(new { membershipRevision, evidenceRevision, related = stats.Revision });
        var binding = Hash(new { owner, membershipRevision, related = true });
        var prior = Decode(request.Cursor, binding, codec);
        CheckRevision(prior, revision, "context_changed");
        var page = await db.Database.SqlQueryRaw<RelatedRow>(RelatedSql + """
            SELECT "Id", blocking AS "Blocking", reasons AS "Reasons" FROM neighbors WHERE "Id" > @last ORDER BY "Id" LIMIT @limit
            """, P("owner", owner), P("ids", ids), P("last", prior?.LastId ?? 0), P("limit", request.Limit + 1)).ToListAsync(ct);
        var pageIds = page.Take(request.Limit).Select(p => p.Id).ToArray();
        var pageSongs = await AlbumReviewContextReader.Project(owned.Where(s => pageIds.Contains(s.Id))).ToDictionaryAsync(s => s.Id, ct);
        var members = evidence.Tracks.Where(t => ids.Contains(t.Id)).ToArray();
        var (facts, correspondences) = ProgramFacts(members, reference);
        var scope = new AlbumReviewContextRequest([], ids, new(Version, ids, request.Reference));
        var planPacket = ready ? evidence with { Revision = revision } : null;
        // The selected tracks and every blocking dependency are immutable across pages.
        // Only shorten the related prefix; its cursor must resume after the last returned row.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            AlbumReviewContext? pageEvidence;
            try
            {
                pageEvidence = pageIds.Length == 0 ? null : await EvidenceAsync(db, files, owner,
                    pageIds.ToDictionary(id => id, id => pageSongs[id]),
                    page.Take(pageIds.Length).ToDictionary(p => p.Id, p => new SortedSet<string>(p.Reasons, StringComparer.Ordinal)),
                    [], [], [], ct, includePrograms: false);
            }
            catch (AlbumReviewReadException error) when (error.StatusCode == 413 && pageIds.Length > 1)
            {
                pageIds = pageIds.Take((pageIds.Length + 1) / 2).ToArray();
                continue;
            }
            var packet = new AlbumAuditContext(Version, DateTime.UtcNow, members,
                pageEvidence?.Tracks.Select(t => new AlbumAuditRelated(t, page.Single(p => p.Id == t.Id).Blocking)).ToArray() ?? [],
                evidence.Tracks.Where(t => !ids.Contains(t.Id)).ToArray(),
                stats.Count, stats.BlockingCount, blockers.Take(200).Select(b => b.Id).ToArray(), true, page.Count <= pageIds.Length,
                page.Count > pageIds.Length ? Encode(new(Version, binding, revision, "", pageIds[^1]), codec) : null,
                reference is null ? "absent" : reference.TrackCountContested || reference.Tracks.Any(t => t.IsContested) ? "contested" : "available",
                references, correspondences, facts, evidence.Duplicates, evidence.Conflicts, ready,
                ready ? new(scope, revision) : null, revision, membershipRevision, evidenceRevision, stats.Revision, false,
                reference is null ? "unknownWithoutReference" : "referenceHypothesisOnly");
            var uniqueTracks = evidence.Tracks.Concat(pageEvidence?.Tracks ?? []).DistinctBy(t => t.Id);
            var rowCount = links.Count + uniqueTracks.Sum(t => t.Changes.Count + t.Candidates.Count)
                + references.Sum(r => r.Program.Tracks.Count + r.Program.Sources.Count + 1);
            if (rowCount <= AlbumReviewContextReader.MaxEvidenceRows
                && JsonSerializer.SerializeToUtf8Bytes(packet, Json).Length <= AlbumReviewContextReader.MaxResponseBytes - 256)
                return new(packet, planPacket);
            // An individual related row that cannot fit must remain an explicit refusal.
            // Never skip it, return an empty nonterminal page, or omit blocking evidence.
            if (pageIds.Length <= 1) throw TooLarge();
            pageIds = pageIds.Take((pageIds.Length + 1) / 2).ToArray();
        }
    }

    private static async Task<AlbumReviewContext> EvidenceAsync(MusicHoarderDbContext db, IFileSystem files, Guid owner,
        Dictionary<int, SongMetadata> songs, Dictionary<int, SortedSet<string>> reasons, int[] ids, string[] froms,
        List<SongDuplicateLink> links, CancellationToken ct, string selectionKind = "explicitTracks", bool includePrograms = true)
    {
        try { return await AlbumReviewContextReader.EvidenceAsync(db, files, owner, songs, reasons, ids, froms, links, ct, selectionKind, includePrograms, preserveOwnedKeeper: true); }
        catch (AlbumReviewReadException e) when (e.StatusCode == 413) { throw TooLarge(); }
    }

    internal static async Task<AlbumReviewContext> ReadPlanAsync(MusicHoarderDbContext db, IFileSystem files, Guid owner,
        AlbumReviewContextRequest scope, CancellationToken ct)
    {
        if (scope.Audit is not { Version: Version } audit || scope.Froms is null || scope.Froms.Length != 0 || scope.TargetSongIds is null || audit.MemberSongIds is null ||
            !scope.TargetSongIds.Order().SequenceEqual(audit.MemberSongIds.Order())) throw Bad("Invalid versioned bounded B scope.");
        if (db.Database.CurrentTransaction is not null)
            await db.Database.ExecuteSqlRawAsync("SET LOCAL jit = off", ct);
        return (await ReadAsync(db, files, owner, new(audit.MemberSongIds, audit.Reference), ct)).PlanPacket
            ?? throw new AlbumReviewReadException(409, "expand_context", "Blocking context exceeds 200 tracks. Review a narrower program; no plan can bypass these dependencies.", 200);
    }

    private static (AlbumAuditFact[], AlbumAuditCorrespondence[]) ProgramFacts(AlbumReviewTrack[] members, AlbumReviewProgram? reference)
    {
        var facts = AlbumReviewContextReader.Conflicts(members).Select(c => new AlbumAuditFact(c.Kind, c.SongIds, Detail: c.Key)).ToList();
        foreach (var field in new[] { "AlbumArtist", "Album", "Year", "MusicBrainzReleaseId", "TotalDiscs", "TotalTracks", "ReleaseTypePrimary" })
            if (members.Select(t => t.Current.Values[field]).Distinct().Count() > 1)
                facts.Add(new("contradictory" + field, members.Select(t => t.Id).ToArray()));
        foreach (var member in members)
        foreach (var candidate in member.Candidates.Where(c => c.Tags is not null))
        {
            foreach (var field in new[] { "Album", "AlbumArtist", "Year", "DiscNumber", "TrackNumber", "TotalTracks", "TotalDiscs", "ReleaseTypePrimary", "ReleaseTypes" })
                if (candidate.Tags!.Values.TryGetValue(field, out var proposed) && proposed is not null &&
                    member.Current.Values.TryGetValue(field, out var current) && !Equals(current, proposed))
                    facts.Add(new("candidateContradiction", [member.Id], Detail: field + ":attempt:" + candidate.AttemptId));
            if (candidate.DurationSeconds is { } seconds && member.DurationMs is { } ms && Math.Abs((long)seconds * 1000 - ms) > 2000)
                facts.Add(new("candidateDurationContradiction", [member.Id], Detail: "attempt:" + candidate.AttemptId));
        }
        var matches = new List<AlbumAuditCorrespondence>();
        if (reference is not null)
        {
            foreach (var member in members)
            {
                var recording = (string?)member.Current.Values["MusicBrainzId"];
                var disc = (int?)member.Current.Values["DiscNumber"];
                var track = (int?)member.Current.Values["TrackNumber"];
                var byId = reference.Tracks.Where(t => !string.IsNullOrEmpty(recording) && t.MusicBrainzRecordingId == recording).ToArray();
                var candidates = byId.Length > 0 ? byId : reference.Tracks.Where(t => disc.HasValue && t.DiscNumber == disc && t.TrackNumber == track).ToArray();
                if (candidates.Length > 1 && disc.HasValue && track.HasValue)
                    candidates = candidates.Where(t => t.DiscNumber == disc && t.TrackNumber == track).ToArray();
                if (candidates.Length != 1) { facts.Add(new(candidates.Length > 1 ? "ambiguousReferenceCorrespondence" : "unmatchedMember", [member.Id])); continue; }
                var match = candidates[0];
                if (member.Current.Values["TotalTracks"] is int total && total != reference.Tracks.Where(t => t.DiscNumber == match.DiscNumber).Select(t => t.TrackNumber).Distinct().Count())
                    facts.Add(new("referenceTrackTotalContradiction", [member.Id], match.DiscNumber, Detail: "referenceEditionUnconfirmed"));
                if (member.Current.Values["TotalDiscs"] is int discs && reference.Tracks.Count > 0 && discs != reference.Tracks.Max(t => t.DiscNumber))
                    facts.Add(new("referenceDiscTotalContradiction", [member.Id], Detail: "referenceEditionUnconfirmed"));
                if (member.Current.Values["AlbumArtist"] is string credit && reference.Artist is not null && credit != reference.Artist)
                    facts.Add(new("referenceCreditContradiction", [member.Id], Detail: "exactDisplayCreditDiffers;referenceEditionUnconfirmed"));
                matches.Add(new(member.Id, match.DiscNumber, match.TrackNumber, byId.Length > 0 ? "recordingId" : "explicitDiscAndPosition", "referenceEditionUnconfirmed"));
                if (member.DurationMs is { } duration && match.DurationMs is { } expected && Math.Abs((long)duration - expected) > 2000)
                    facts.Add(new("durationContradiction", [member.Id], match.DiscNumber, match.TrackNumber));
                if (disc.HasValue && disc != match.DiscNumber) facts.Add(new("discContradiction", [member.Id], match.DiscNumber, match.TrackNumber));
            }
            foreach (var track in reference.Tracks.Where(t => !matches.Any(m => m.DiscNumber == t.DiscNumber && m.TrackNumber == t.TrackNumber)))
                facts.Add(new("uncoveredReferencePosition", [], track.DiscNumber, track.TrackNumber, track.IsContested ? "contestedReferenceTrack" : "referenceHypothesisOnly"));
        }
        return (facts.ToArray(), matches.ToArray());
    }

    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, RevisionJson)));
    private static NpgsqlParameter P(string name, object value) => new(name, value);
    private static int ParseStatus(string? value) => string.IsNullOrEmpty(value) ? -1 :
        Enum.TryParse<EnrichmentStatus>(value, true, out var parsed) && Enum.IsDefined(parsed) ? (int)parsed : throw Bad("Unknown enrichment status.");
    private static void CheckOwner(Guid owner) { if (owner == Guid.Empty) throw new AlbumReviewReadException(401, "unauthenticated", "A signed-in owner is required."); }
    private static string Encode(Cursor cursor, AlbumAuditCursorCodec? codec) => (codec ?? AlbumAuditCursorCodec.ForTests).Protect(JsonSerializer.Serialize(cursor, Json));
    private static Cursor? Decode(string? value, string binding, AlbumAuditCursorCodec? codec)
    {
        if (value is null) return null;
        if (value.Length > 8192) throw Bad("Cursor exceeds 8192 characters.");
        try
        {
            var cursor = JsonSerializer.Deserialize<Cursor>((codec ?? AlbumAuditCursorCodec.ForTests).Unprotect(value), Json);
            if (cursor is null || cursor.Version != Version || cursor.Binding != binding || cursor.Revision is null || cursor.Key is null || cursor.LastId < 0) throw Bad("Invalid or foreign cursor.");
            return cursor;
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException) { throw Bad("Invalid cursor."); }
    }
    private static void CheckRevision(Cursor? cursor, string revision, string code)
    { if (cursor is not null && cursor.Revision != revision) throw new AlbumReviewReadException(409, code, "The dependency snapshot changed; restart pagination."); }
    private static AlbumReviewReadException Bad(string message) => new(400, "invalid_selection", message);
    private static AlbumReviewReadException TooLarge() => new(413, "evidence_too_large", "Evidence exceeds the row or byte budget; select a narrower explicit program. No partial evidence returned.", AlbumReviewContextReader.MaxEvidenceRows);
}
