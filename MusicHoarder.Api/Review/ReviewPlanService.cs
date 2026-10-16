using System.Data;
using System.IO.Abstractions;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review;

internal sealed class ReviewPlanException(int statusCode, ReviewPlanError error) : Exception(error.Message)
{
    internal int StatusCode { get; } = statusCode;
    internal ReviewPlanError Error { get; } = error;
}

/// <summary>Exact metadata plans with a transactionally linked targeted copy outbox; no provider or copy writer here.
/// PostgreSQL retries own the entire serializable transaction, including disposal and reload.</summary>
internal static class ReviewPlanService
{
    internal const string CopyState = "awaitingVerifiedBuild";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static Task<ReviewPlanPreview> PreviewAsync(MusicHoarderDbContext db, IFileSystem files,
        IDestinationPathResolver resolver, Guid owner, ReviewPlanRequest request, CancellationToken ct)
    {
        ValidateRequest(request);
        var id = Guid.NewGuid();
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            // A retry after an uncertain commit returns the original immutable preview.
            var saved = await db.ReviewPlans.SingleOrDefaultAsync(p => p.Id == id && p.OwnerUserId == owner, token);
            if (saved is not null) return ReadPreview(saved);
            var context = await AlbumReviewContextReader.ReadAsync(db, files, owner, request.Scope, token);
            if (context.Revision != request.ContextRevision)
                throw Error(409, "context_changed", "Read the album context again before preparing this plan.",
                    [new("contextRevision", null, null, request.ContextRevision, context.Revision)]);
            var preview = await BuildPreview(db, files, resolver, owner, id, request, context, token);
            db.ReviewPlans.Add(new ReviewPlan
            {
                Id = id, OwnerUserId = owner, ActorUserId = owner, CreatedAtUtc = DateTime.UtcNow,
                Revision = preview.Revision, RequestJson = Serialize(request), ContextJson = Serialize(context),
                PreviewJson = Serialize(preview)
            });
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return preview;
        }, ct);
    }

    internal static async Task<ReviewPlanPreview> GetAsync(MusicHoarderDbContext db, Guid owner, Guid id, CancellationToken ct)
        => ReadPreview(await db.ReviewPlans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.OwnerUserId == owner, ct)
            ?? throw Error(404, "plan_not_found", "Plan unavailable to this owner."));

    internal static Task<ReviewPlanResult> ApplyAsync(MusicHoarderDbContext db, IFileSystem files,
        IDestinationPathResolver resolver, Guid owner, Guid id, string revision, CancellationToken ct, Guid? requestId = null)
    {
        return ApplyGuardedAsync();
        async Task<ReviewPlanResult> ApplyGuardedAsync()
        {
        await using var guard = await ReviewWriteGuard.AcquireAsync(db, wait: false, ct)
            ?? throw Error(409, "writer_busy", "A library writer is active. Retry Apply after it finishes; nothing was applied.");
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var plan = await db.ReviewPlans.SingleOrDefaultAsync(p => p.Id == id && p.OwnerUserId == owner, token)
                ?? throw Error(404, "plan_not_found", "Plan unavailable to this owner.");
            if (string.IsNullOrEmpty(revision) || revision != plan.Revision)
                throw Error(409, "plan_revision_changed", "Supply the exact immutable preview revision.");
            var request = JsonSerializer.Deserialize<ReviewPlanRequest>(plan.RequestJson, Json)!;
            var ids = request.Tracks.Select(t => t.SongId).Order().ToArray();
            var key = requestId ?? id;
            var keyed = await db.ReviewCopyRequestKeys.SingleOrDefaultAsync(j => j.OwnerUserId == owner && j.RequestId == key, token);
            if (keyed is not null && (keyed.PlanId != id || keyed.Revision != revision))
                throw Error(409, "request_key_reused", "This request key belongs to different content.");
            var existingJob = await db.ReviewCopyJobs.SingleOrDefaultAsync(j => j.OwnerUserId == owner && j.PlanId == id, token);
            if (existingJob is not null)
            {
                if (existingJob.State is "rolledBack" or "rollingBack")
                    throw Error(409, "plan_rolled_back", "Prepare a new plan after rollback.");
                if (keyed is null)
                {
                    db.ReviewCopyRequestKeys.Add(new() { OwnerUserId = owner, RequestId = key, PlanId = id, Revision = revision, JobId = existingJob.Id });
                    await db.SaveChangesAsync(token); await tx.CommitAsync(token);
                }
                return new ReviewPlanResult(id, revision, "metadataApplied", ids, CopyState, existingJob.Id);
            }
            if (plan.AppliedAtUtc is not null)
            {
                var adopted = await ReviewCopyJobs.EnqueueAsync(db, files, plan, ReadPreview(plan), key, token);
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
                return new ReviewPlanResult(id, revision, "metadataApplied", ids, CopyState, adopted.Id);
            }
            var before = JsonSerializer.Deserialize<AlbumReviewContext>(plan.ContextJson, Json)!;
            AlbumReviewContext current;
            try { current = await AlbumReviewContextReader.ReadAsync(db, files, owner, request.Scope, token); }
            catch (AlbumReviewReadException e) when (e.StatusCode is 404 or 413)
            {
                throw Error(409, "plan_stale", "The reviewed selection is no longer available within its original bounds.",
                    [new("selection", null, null, before.Scope, e.Error)]);
            }
            if (current.Revision != before.Revision)
                throw Error(409, "plan_stale", "Album evidence or membership changed. Prepare a new preview.", Differences(before, current));
            var preview = await BuildPreview(db, files, resolver, owner, id, request, current, token);
            if (preview.Revision != plan.Revision)
                throw Error(409, "plan_stale", "Destination policy, source or destination observations changed. Prepare a new preview.",
                    [new("preview", null, null, ReadPreview(plan), preview)]);
            if (!preview.CanApply) throw Error(409, "plan_conflicts", "Resolve the preview's conflicts in an explicit new plan.");
            var songs = await db.Songs.Where(s => s.OwnerUserId == owner && s.DeletedAtUtc == null && ids.Contains(s.Id)).ToListAsync(token);
            if (songs.Count != ids.Length) throw Error(409, "plan_stale", "A selected track is unavailable.");
            await ManualReviewMetadata.LoadAsync(db, songs, token);
            var originals = songs.ToDictionary(s => s.Id, ReviewCopyJobs.Snapshot);
            foreach (var song in songs)
            {
                var patch = request.Tracks.Single(t => t.SongId == song.Id);
                // Carry forward only still-active explicit approvals. Omitted fields remain exactly
                // as stored; this is not a new musical decision about any neighboring member.
                var approved = song.ManualReviewValues.Where(p => song.HasManualReviewValue(p.Key))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                var previous = ReviewFields.Values(song);
                song.CaptureOriginalMetadata();
                song.LockManualApproval();
                foreach (var (field, value) in patch.Fields)
                {
                    ReviewFields.Set(song, field, ReviewFields.Parse(field, value));
                    approved[field] = value.ValueKind == JsonValueKind.Null ? null : ReviewFields.Text(song, field);
                }
                foreach (var (field, value) in approved)
                {
                    var audit = ManualReviewMetadata.Record(db, song, field, AsText(previous[field]), value);
                    audit.ActorUserId = owner;
                    audit.Reason = request.Reason;
                    audit.EvidenceReferencesJson = Serialize(request.EvidenceReferences);
                    audit.ReviewPlanRevision = revision;
                }
                // Even an approve-only plan has a durable receipt (ReviewPlans). No invented field audit.
                song.ManualReviewValues = approved;
                if (patch.PreserveAlbumMembership) song.ClearDuplicate();
                song.ReviewPlanId = id;
                song.EnrichmentStatus = EnrichmentStatus.Matched;
                song.EnrichmentError = null;
                song.LibraryBuildStatus = LibraryBuildStatus.AwaitingReviewBuild;
                // Keep current AND previous copies and all historical build facts until stage C verifies replacements.
            }
            plan.AppliedAtUtc = DateTime.UtcNow;
            var job = await ReviewCopyJobs.EnqueueAsync(db, files, plan, preview, key, token, originals);
            await guard.CheckAsync(token);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return new ReviewPlanResult(id, revision, "metadataApplied", ids, CopyState, job.Id);
        }, ct);
        }
    }

    private static async Task<ReviewPlanPreview> BuildPreview(MusicHoarderDbContext db, IFileSystem files,
        IDestinationPathResolver resolver, Guid owner, Guid id, ReviewPlanRequest request,
        AlbumReviewContext context, CancellationToken ct)
    {
        var selected = context.Scope.TargetSongIds.ToHashSet();
        if (request.Tracks.Any(t => !selected.Contains(t.SongId)))
            throw Error(400, "outside_selection", "Every edited track must be an explicit target of the reviewed scope.");
        var contextIds = context.Tracks.Select(t => t.Id).ToArray();
        var songs = await AlbumReviewContextReader.Project(db.Songs.AsNoTracking()
            .Where(s => s.OwnerUserId == owner && s.DeletedAtUtc == null && contextIds.Contains(s.Id))).ToListAsync(ct);
        await ManualReviewMetadata.LoadAsync(db, songs, ct);
        var tracks = new List<ReviewPlanTrackPreview>();
        foreach (var patch in request.Tracks.OrderBy(t => t.SongId))
        {
            var song = songs.Single(s => s.Id == patch.SongId);
            if (song.EnrichmentStatus is not (EnrichmentStatus.Matched or EnrichmentStatus.NeedsReview) || song.IsSynthetic)
                throw Error(400, "invalid_track_state", "Plans accept real Matched or NeedsReview tracks only.");
            if (song.EnrichmentStatus == EnrichmentStatus.NeedsReview && !patch.Approve)
                throw Error(400, "approval_required", "NeedsReview tracks require an explicit approval.");
            if (song.IsDuplicate && !patch.PreserveAlbumMembership)
                throw Error(400, "membership_decision_required", "Explicitly preserve this album membership before building a duplicate-marked track.");
            var before = ReviewFields.Values(song);
            foreach (var (field, value) in patch.Fields) ReviewFields.Set(song, field, ReviewFields.Parse(field, value));
            ReviewFields.Validate(song, patch.Fields.Keys.ToHashSet(StringComparer.Ordinal));
            tracks.Add(new(song.Id, song.EnrichmentStatus.ToString(), "Matched",
                patch.Fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ReviewPlanFieldDiff(p.Key,
                    before[p.Key], p.Value.ValueKind == JsonValueKind.Null ? null : ReviewFields.Get(song, p.Key),
                    p.Value.ValueKind == JsonValueKind.Null ? "explicitUnknown" : "approvedValue")).ToArray(),
                song.DestinationPath, resolver.ResolvePath(song), CopyState,
                AlbumReviewContextReader.Parent(song.SourcePath)!,
                new((string?)before["AlbumArtist"] ?? (string?)before["Artist"], (string?)before["Album"]),
                new(song.AlbumArtist ?? song.Artist, song.Album)));
        }
        var paths = songs.ToDictionary(s => s.Id, resolver.ResolvePath);
        // A new identity can bring previously unrelated owned rows into play. Never silently
        // harmonize them or omit them from the dependency set: require a wider reviewed context.
        Expression<Func<SongMetadata, bool>> relevant = s => false;
        foreach (var song in songs.Where(s => request.Tracks.Any(t => t.SongId == s.Id)))
        {
            var artist = song.AlbumArtist ?? song.Artist;
            var album = song.Album;
            var path = paths[song.Id].ToLowerInvariant();
            Expression<Func<SongMetadata, bool>> term = s => s.DestinationPath != null && s.DestinationPath.ToLower() == path
                || (album != null && artist != null && s.Album == album && (s.AlbumArtist ?? s.Artist) == artist);
            relevant = Or(relevant, term);
        }
        var outsiders = await db.Songs.AsNoTracking().Where(s => s.OwnerUserId == owner && s.DeletedAtUtc == null)
            .Where(relevant).Where(s => !contextIds.Contains(s.Id)).OrderBy(s => s.Id)
            .Select(s => s.Id).Take(AlbumReviewContextReader.MaxTracks + 1).ToArrayAsync(ct);
        if (outsiders.Length > 0) throw Error(409, "expand_context", "Proposed groups or paths include other owned tracks. Read them in the scope before previewing.",
            [new("additionalContextSongIds", null, null, contextIds, outsiders)]);
        var final = context.Tracks.Select(t =>
        {
            var song = songs.Single(s => s.Id == t.Id);
            // Raw final values preserve explicit null (including the bool sentinel) from the request.
            var values = ReviewFields.Values(song);
            var patch = request.Tracks.SingleOrDefault(p => p.SongId == song.Id);
            if (patch is not null) foreach (var (field, value) in patch.Fields)
                values[field] = value.ValueKind == JsonValueKind.Null ? null : ReviewFields.Get(song, field);
            return t with { Current = t.Current with { Values = values }, Copy = t.Copy with { Path = paths[t.Id] } };
        }).ToArray();
        var conflicts = AlbumReviewContextReader.Conflicts(final).Select(conflict =>
        {
            if (conflict.Kind is not ("albumPosition" or "possibleAlbumPosition")) return conflict;
            var members = songs.Where(s => conflict.SongIds.Contains(s.Id)).ToArray();
            var separatePaths = members.Select(s => paths[s.Id]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == members.Length;
            var allDismissed = members.All(a => members.Where(b => b.Id != a.Id).All(b => context.Duplicates.Any(d =>
                d.Status == "Dismissed" && (d.SongIdLow == Math.Min(a.Id, b.Id) && d.SongIdHigh == Math.Max(a.Id, b.Id)))));
            if (separatePaths && allDismissed) return conflict with { Kind = "distinctTakesRetained" };
            var separateEditions = members.All(s => !string.IsNullOrEmpty(s.MusicBrainzReleaseId))
                && members.Select(s => s.MusicBrainzReleaseId).Distinct().Count() == members.Length
                && members.Select(s => files.Path.GetDirectoryName(paths[s.Id])).Distinct(StringComparer.OrdinalIgnoreCase).Count() == members.Length;
            return separateEditions ? conflict with { Kind = "distinctEditionsRetained" } : conflict;
        }).ToList();
        var observations = tracks.SelectMany(t => new[] { t.PlannedPath, songs.Single(s => s.Id == t.SongId).SourcePath })
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(p => AlbumReviewContextReader.ObserveFile(files, p)).ToArray();
        foreach (var track in tracks)
        {
            var observation = observations.Single(o => o.Path == track.PlannedPath);
            if (observation.Presence != "missing" && !context.Tracks.Any(t => t.Id == track.SongId
                && (string.Equals(t.Copy.Path, track.PlannedPath, StringComparison.Ordinal)
                    || string.Equals(t.Copy.Path, track.PlannedPath, StringComparison.OrdinalIgnoreCase)
                        && files.File.Exists(t.Copy.Path!)
                        && ReviewCopyJobs.HashIfPresent(files, t.Copy.Path!) == ReviewCopyJobs.HashIfPresent(files, track.PlannedPath))))
                conflicts.Add(new("destinationOccupiedOrUnknown", track.PlannedPath, [track.SongId]));
        }
        var fingerprints = tracks.SelectMany(t => new[] { t.PlannedPath, songs.Single(s => s.Id == t.SongId).SourcePath,
                songs.Single(s => s.Id == t.SongId).DestinationPath, songs.Single(s => s.Id == t.SongId).PreviousDestinationPath })
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(p =>
            {
                try { var hash = ReviewCopyJobs.HashIfPresent(files, p); return new ReviewPlanFileFingerprint(p, hash is null ? "missing" : "present", hash); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new ReviewPlanFileFingerprint(p, "unavailable", null); }
            }).ToArray();
        if (fingerprints.Any(f => f.Presence == "unavailable"))
            conflicts.Add(new("fileFingerprintUnavailable", "selectedFiles", request.Tracks.Select(t => t.SongId).ToArray()));
        var preview = new ReviewPlanPreview(id, "", context.Revision, tracks, contextIds, conflicts,
            !conflicts.Any(c => c.Kind is not ("distinctTakesRetained" or "distinctEditionsRetained")
                && c.SongIds.Any(s => request.Tracks.Any(t => t.SongId == s))), CopyState, observations, "exact-verified-copy-v2", fingerprints);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { request, preview }, Json);
        if (bytes.Length > AlbumReviewContextReader.MaxResponseBytes)
            throw Error(413, "plan_limit", "Plan exceeds the byte budget; no partial preview was stored.");
        return preview with { Revision = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    }

    private static Expression<Func<SongMetadata, bool>> Or(Expression<Func<SongMetadata, bool>> left, Expression<Func<SongMetadata, bool>> right)
        => Expression.Lambda<Func<SongMetadata, bool>>(Expression.OrElse(left.Body,
            new ReplaceParameter(right.Parameters[0], left.Parameters[0]).Visit(right.Body)), left.Parameters);
    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    private static IReadOnlyList<ReviewPlanDrift> Differences(AlbumReviewContext expected, AlbumReviewContext actual)
    {
        // Recursive JSON diff also covers audit, candidates, programs, duplicate decisions and copies.
        // Bounded by the same context limits; the response names the exact changed JSON path.
        var changes = new List<ReviewPlanDrift>();
        Walk(JsonSerializer.SerializeToElement(expected with { ObservedAtUtc = default, Revision = "" }, Json),
            JsonSerializer.SerializeToElement(actual with { ObservedAtUtc = default, Revision = "" }, Json), "$", null);
        return changes;
        void Walk(JsonElement a, JsonElement b, string path, int? songId)
        {
            if (a.ValueKind == b.ValueKind && (a.ValueKind == JsonValueKind.Undefined || a.GetRawText() == b.GetRawText())) return;
            if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
            {
                if (a.TryGetProperty("id", out var key) && a.TryGetProperty("sourcePath", out _)) songId = key.GetInt32();
                foreach (var name in a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name)))
                    Walk(a.TryGetProperty(name, out var x) ? x : default, b.TryGetProperty(name, out var y) ? y : default, path + "." + name, songId);
            }
            else if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array && a.GetArrayLength() == b.GetArrayLength())
            {
                for (var i = 0; i < a.GetArrayLength(); i++) Walk(a[i], b[i], $"{path}[{i}]", songId);
            }
            else changes.Add(new("context", songId, path, a.ValueKind == JsonValueKind.Undefined ? null : a.Clone(),
                b.ValueKind == JsonValueKind.Undefined ? null : b.Clone()));
        }
    }

    private static void ValidateRequest(ReviewPlanRequest r)
    {
        if (r.Scope is null || string.IsNullOrWhiteSpace(r.ContextRevision) || r.ContextRevision.Length != 64
            || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 1024 || r.Reason.Any(char.IsControl)
            || r.EvidenceReferences is null || r.EvidenceReferences.Length is < 1 or > 16
            || r.EvidenceReferences.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 1024 || e.Any(char.IsControl))
            || r.Tracks is null || r.Tracks.Length is < 1 or > AlbumReviewContextReader.MaxTracks
            || r.Tracks.Any(t => t is null || t.SongId <= 0 || t.Fields is null || t.Fields.Count > ReviewFields.Names.Length
                || (!t.Approve && !t.PreserveAlbumMembership && t.Fields.Count == 0)) || r.Tracks.Select(t => t.SongId).Distinct().Count() != r.Tracks.Length)
            throw Error(400, "invalid_plan", "Supply a bounded scope/revision, reason, 1..16 proof references and 1..200 unique explicit track changes or approvals.");
        foreach (var track in r.Tracks) foreach (var (name, value) in track.Fields) ReviewFields.Parse(name, value);
    }
    private static ReviewPlanPreview ReadPreview(ReviewPlan plan) => JsonSerializer.Deserialize<ReviewPlanPreview>(plan.PreviewJson, Json)!;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static string? AsText(object? value) => value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    private static ReviewPlanException Error(int status, string code, string message, IReadOnlyList<ReviewPlanDrift>? diff = null) => new(status, new(code, message, diff));
}
