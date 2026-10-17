using System.Data;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review.Copy;

namespace MusicHoarder.Api.Review.Audit;

internal static class CopyAuditJobs
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly string[] Terminal = ["verified", "issues", "unverifiable", "stale", "canceled"];
    internal static DateTime Now => ReviewTimestamp.Persisted(DateTime.UtcNow);
    internal static string DbRevision(SongMetadata song) => AlbumAuditReader.Hash(new
    {
        snapshot = ReviewCopyJobs.Snapshot(song), song.DestinationPath, song.PreviousDestinationPath,
        song.FileSizeBytes, song.LastModifiedUtc, song.DurationMs, song.DurationSeconds,
        song.LibraryBuildAttempts, song.LibraryBuildLastAttemptedAtUtc
    });
    internal static AlbumReviewContextRequest Normalize(AlbumReviewContextRequest scope)
    {
        if (scope?.Audit?.MemberSongIds?.Length > 200) throw Error(413, "scope_too_large", "Select at most 200 explicit members.");
        if (scope?.Audit is not { Version: AlbumAuditReader.Version } audit || scope.Froms is null || scope.Froms.Length != 0 ||
            audit.MemberSongIds is null || scope.TargetSongIds is null || audit.MemberSongIds.Length is < 1 or > 200 ||
            audit.MemberSongIds.Any(i => i <= 0) || audit.MemberSongIds.Distinct().Count() != audit.MemberSongIds.Length ||
            scope.TargetSongIds.Distinct().Count() != scope.TargetSongIds.Length || !scope.TargetSongIds.Order().SequenceEqual(audit.MemberSongIds.Order()))
            throw Error(400, "invalid_scope", "Use the explicit versioned scope returned by E for 1..200 unique owned members.");
        var ids = audit.MemberSongIds.Order().ToArray();
        return new([], ids, audit with { MemberSongIds = ids });
    }

    internal static Task<CopyAuditJobResult> CreateAsync(MusicHoarderDbContext db, IFileSystem files, Guid owner,
        CopyAuditRequest request, CancellationToken ct)
    {
        var scope = Normalize(request.Scope);
        if (request.RequestId == Guid.Empty || request.ContextRevision is null || request.ContextRevision.Length != 64 || !request.ContextRevision.All(Uri.IsHexDigit))
            throw Error(400, "invalid_request", "Use a nonempty request UUID and the fresh E contextRevision.");
        var hash = AlbumAuditReader.Hash(new { kind = "create", scope, request.ContextRevision });
        return CreateCore(db, files, owner, request.RequestId, hash, scope, request.ContextRevision, null, null, ct);
    }

    internal static async Task<CopyAuditJobResult> RetryAsync(MusicHoarderDbContext db, IFileSystem files, Guid owner,
        Guid parentId, CopyAuditRetryRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.SongIds is null || request.SongIds.Length is < 1 or > 200 ||
            request.SongIds.Any(i => i <= 0) || request.SongIds.Distinct().Count() != request.SongIds.Length)
            throw Error(400, "invalid_retry", "Use a new nonempty UUID and 1..200 unique failed/stale IDs.");
        var ids = request.SongIds.Order().ToArray();
        var parent = await Owned(db, owner, parentId, ct);
        var scope = Normalize(JsonSerializer.Deserialize<AlbumReviewContextRequest>(parent.ScopeJson, Json)!);
        var hash = AlbumAuditReader.Hash(new { kind = "retry", parentId, ids });
        return await CreateCore(db, files, owner, request.RequestId, hash, scope, null, parentId, ids, ct);
    }

    private static async Task<CopyAuditJobResult> CreateCore(MusicHoarderDbContext db, IFileSystem files, Guid owner,
        Guid requestId, string hash, AlbumReviewContextRequest scope, string? expectedRevision,
        Guid? parentId, int[]? retryIds, CancellationToken ct)
    {
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(owner.ToString("N") + requestId.ToString("N")));
        var key = 0x4d48415500000000L | BinaryPrimitives.ReadUInt32BigEndian(keyBytes);
        await using var requestGate = await ReviewWriteGuard.AcquireKeyAsync(db, key, true, ct);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var existing = await db.CopyAuditJobs.AsNoTracking().SingleOrDefaultAsync(j => j.OwnerUserId == owner && j.RequestId == requestId, token);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) throw Error(409, "request_key_reused", "This request UUID already describes different work.");
            await tx.CommitAsync(token);
            return await GetAsync(db, owner, existing.Id, 0, 25, token);
        }
        if (parentId is { } parent)
        {
            var old = await Owned(db, owner, parent, token);
            if (old.State is "queued" or "running" || old.CancelRequested) throw Error(409, "audit_busy", "Retry requires a stopped, uncanceled audit.");
            var eligible = await db.CopyAuditTracks.Where(t => t.JobId == parent && retryIds!.Contains(t.SongId)).ToListAsync(token);
            if (eligible.Count != retryIds!.Length || eligible.Any(t => t.State is not ("issues" or "unverifiable" or "stale")))
                throw Error(400, "invalid_retry", "Only explicitly selected issues, unverifiable or stale results can be retried. Verified tracks require a new full audit.");
        }
        AlbumReviewContext context;
        try { context = await AlbumAuditReader.ReadPlanAsync(db, files, owner, scope, token); }
        catch (AlbumReviewReadException e) when (e.Error.Code == "reference_changed")
        { throw Error(409, "context_changed", "Select the current cached reference in a new E context before auditing."); }
        if (expectedRevision is not null && context.Revision != expectedRevision)
            throw Error(409, "context_changed", "Read the fresh explicit E scope before enqueueing. No partial audit was created.");
        var ids = retryIds ?? scope.Audit!.MemberSongIds;
        var songs = await AlbumReviewContextReader.Project(db.Songs.AsNoTracking().Where(s => s.OwnerUserId == owner && s.DeletedAtUtc == null && ids.Contains(s.Id))).ToListAsync(token);
        if (songs.Count != ids.Length) throw Error(404, "selection_not_found", "All audit members must remain live and owned.");
        await ManualReviewMetadata.LoadAsync(db, songs, token);
        var job = new CopyAuditJob { Id = Guid.NewGuid(), OwnerUserId = owner, RequestId = requestId, RequestHash = hash,
            ScopeJson = JsonSerializer.Serialize(scope, Json), ExpectedContextRevision = context.Revision, ParentJobId = parentId,
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
            Tracks = songs.OrderBy(s => s.Id).Select(s => new CopyAuditTrack { SongId = s.Id, SourcePath = s.SourcePath,
                DestinationPath = s.DestinationPath, ExpectedDbRevision = DbRevision(s), UpdatedAtUtc = Now }).ToList() };
        db.CopyAuditJobs.Add(job); await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        return await GetAsync(db, owner, job.Id, 0, 25, token);
    }, ct);
    }

    internal static async Task<CopyAuditJobResult> GetAsync(MusicHoarderDbContext db, Guid owner, Guid id,
        int after, int limit, CancellationToken ct)
    {
        if (after < 0 || limit is < 1 or > 100) throw Error(400, "invalid_page", "Use afterSongId >= 0 and limit 1..100.");
        var job = await Owned(db, owner, id, ct);
        var rows = await db.CopyAuditTracks.AsNoTracking().Where(t => t.JobId == id && t.SongId > after).OrderBy(t => t.SongId).Take(limit + 1).ToListAsync(ct);
        var counts = await db.CopyAuditTracks.Where(t => t.JobId == id).GroupBy(t => t.State).Select(g => new { State = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.State, g => g.Count, ct);
        // Only technical receipt scalars are aggregated; GET never reads/hashes files or runs tools.
        var metrics = await db.CopyAuditAttempts.Where(a => a.JobId == id).GroupBy(a => a.JobId)
            .Select(g => new { Bytes = g.Sum(a => a.BytesHashed), Decodes = g.Sum(a => a.DecodeAttempts), Hits = g.Sum(a => a.CacheHits) }).SingleOrDefaultAsync(ct);
        var active = await db.CopyAuditTracks.CountAsync(t => t.State == "running" && db.CopyAuditJobs.Any(j => j.Id == t.JobId && j.OwnerUserId == owner), ct);
        var queued = await db.CopyAuditJobs.CountAsync(j => j.OwnerUserId == owner && (j.State == "queued" || j.State == "running"), ct);
        return new(job.Id, job.RequestId, job.ParentJobId, JsonSerializer.Deserialize<AlbumReviewContextRequest>(job.ScopeJson, Json)!,
            await db.CopyAuditTracks.Where(t => t.JobId == id).OrderBy(t => t.SongId).Select(t => t.SongId).ToArrayAsync(ct), job.State, job.ExpectedContextRevision, job.ObservedContextRevision,
            job.ScopeState, job.CreatedAtUtc, job.UpdatedAtUtc, Now, job.NextAttemptAtUtc, job.WaitReason, job.CancelRequested,
            counts.Values.Sum(), counts, rows.Take(limit).Select(t => new CopyAuditTrackResult(t.SongId, t.State, t.Attempts,
                t.FailureCode, t.UpdatedAtUtc, t.NextAttemptAtUtc, t.ReceiptJson is null ? null : JsonSerializer.Deserialize<CopyAuditReceipt>(t.ReceiptJson, Json))).ToArray(),
            rows.Count > limit ? rows[limit - 1].SongId : null, "immutable-target-IDs-ascending; live dated state; historical receipts are not a new physical observation",
            metrics?.Bytes ?? 0, metrics?.Decodes ?? 0, metrics?.Hits ?? 0, active, queued, "persisted attempt receipts; unacknowledged I/O interrupted by process/database loss may be unreported");
    }

    internal static async Task<CopyAuditAttemptsPage> AttemptsAsync(MusicHoarderDbContext db, Guid owner, Guid id,
        int songId, int after, int limit, CancellationToken ct)
    {
        await Owned(db, owner, id, ct);
        if (after < 0 || limit is < 1 or > 100) throw Error(400, "invalid_page", "Use afterAttempt >= 0 and limit 1..100.");
        if (!await db.CopyAuditTracks.AnyAsync(t => t.JobId == id && t.SongId == songId, ct)) throw Error(404, "track_not_found", "Track unavailable in this audit.");
        var rows = await db.CopyAuditAttempts.AsNoTracking().Where(a => a.JobId == id && a.SongId == songId && a.Number > after).OrderBy(a => a.Number).Take(limit + 1).ToListAsync(ct);
        return new(songId, rows.Take(limit).Select(a => new CopyAuditAttemptResult(a.Number, a.State, a.StartedAtUtc, a.FinishedAtUtc,
            a.ReceiptJson is null ? null : JsonSerializer.Deserialize<CopyAuditReceipt>(a.ReceiptJson, Json))).ToArray(), rows.Count > limit ? rows[limit - 1].Number : null);
    }

    internal static async Task CancelAsync(MusicHoarderDbContext db, Guid owner, Guid id, CancellationToken ct)
    {
        var job = await Owned(db, owner, id, ct);
        if (job.State is "completed" or "completedWithIssues" or "canceled") return;
        var now = Now;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH canceled_job AS (
              UPDATE "CopyAuditJobs" SET "CancelRequested" = true, "State" = 'canceled', "NextAttemptAtUtc" = NULL, "UpdatedAtUtc" = {now}
              WHERE "Id" = {id} AND "OwnerUserId" = {owner} AND "State" IN ('queued','running') RETURNING "Id"
            ), canceled_tracks AS (
              UPDATE "CopyAuditTracks" SET "State" = 'canceled', "NextAttemptAtUtc" = NULL, "UpdatedAtUtc" = {now}
              WHERE "JobId" IN (SELECT "Id" FROM canceled_job) AND "State" IN ('queued','running','retryWaiting') RETURNING "SongId"
            )
            UPDATE "CopyAuditAttempts" SET "State" = 'canceled', "FinishedAtUtc" = {now}
              WHERE "JobId" = {id} AND "State" = 'running' AND "SongId" IN (SELECT "SongId" FROM canceled_tracks)
            """, ct);

    }
    internal static async Task<CopyAuditJob> Owned(MusicHoarderDbContext db, Guid owner, Guid id, CancellationToken ct)
        => await db.CopyAuditJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id && j.OwnerUserId == owner, ct)
            ?? throw Error(404, "audit_not_found", "Audit unavailable to this owner.");
    internal static ReviewPlanException Error(int status, string code, string message) => new(status, new(code, message));
}
