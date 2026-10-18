using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review.Copy;

internal sealed record ReviewSongSnapshot(Dictionary<string, JsonElement> Fields, Guid? PlanId,
    bool IsManuallyApproved, DateTime? ApprovedAtUtc, EnrichmentStatus Status,
    string SourcePath, bool IsDuplicate, int? DuplicateOfId, Dictionary<string, string?> ManualValues,
    LibraryBuildStatus BuildStatus, DateTime? BuiltAtUtc);

internal static class ReviewCopyJobs
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Snapshot(SongMetadata song) => JsonSerializer.Serialize(new ReviewSongSnapshot(
        ReviewFields.Values(song).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
        song.ReviewPlanId, song.IsManuallyApproved, ReviewTimestamp.Persisted(song.ManuallyApprovedAtUtc), song.EnrichmentStatus,
        song.SourcePath, song.IsDuplicate, song.DuplicateOfId, song.ManualReviewValues.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(), song.LibraryBuildStatus, ReviewTimestamp.Persisted(song.LibraryBuiltAtUtc)), Json);
    internal static ReviewSongSnapshot Read(string json)
    {
        var snapshot = JsonSerializer.Deserialize<ReviewSongSnapshot>(json, Json)!;
        // Jobs recorded before C-R1 can contain 100 ns fractions. Normalize on read
        // as well as on write so an existing stopped job can be retried or rolled back.
        return snapshot with { ApprovedAtUtc = ReviewTimestamp.Persisted(snapshot.ApprovedAtUtc),
            BuiltAtUtc = ReviewTimestamp.Persisted(snapshot.BuiltAtUtc) };
    }
    internal static bool Matches(SongMetadata song, string json)
    {
        var expected = Read(json);
        var actual = Read(Snapshot(song)) with { BuildStatus = expected.BuildStatus, BuiltAtUtc = expected.BuiltAtUtc };
        return JsonSerializer.Serialize(actual, Json) == JsonSerializer.Serialize(expected, Json) && !song.IsDeleted;
    }

    internal static async Task<ReviewCopyJob> EnqueueAsync(MusicHoarderDbContext db, IFileSystem files,
        ReviewPlan plan, ReviewPlanPreview preview, Guid requestId, CancellationToken ct,
        IReadOnlyDictionary<int, string>? before = null)
    {
        if (requestId == Guid.Empty) throw new ReviewPlanException(400, new("invalid_request_key", "Use a nonempty request UUID."));
        var job = new ReviewCopyJob { Id = Guid.NewGuid(), PlanId = plan.Id, OwnerUserId = plan.OwnerUserId,
            Revision = plan.Revision, RequestId = requestId, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        var ids = preview.Tracks.Select(t => t.SongId).ToArray();
        var songs = await db.Songs.Where(s => s.OwnerUserId == plan.OwnerUserId && ids.Contains(s.Id)).ToListAsync(ct);
        await ManualReviewMetadata.LoadAsync(db, songs.Where(s => db.Entry(s).State == EntityState.Unchanged).ToArray(), ct);
        foreach (var track in preview.Tracks)
        {
            var song = songs.Single(s => s.Id == track.SongId);
            if (song.ReviewPlanId != plan.Id) throw new ReviewPlanException(409, new("plan_superseded", "A later correction owns this track."));
            var directory = files.Path.Combine(files.Path.GetDirectoryName(track.PlannedPath)!, ".musichoarder-review", job.Id.ToString("N"), song.Id.ToString());
            var extension = files.Path.GetExtension(track.PlannedPath);
            job.Tracks.Add(new ReviewCopyTrack { JobId = job.Id, SongId = song.Id, SourcePath = song.SourcePath,
                DestinationPath = track.PlannedPath, StagingPath = files.Path.Combine(directory, "candidate" + extension),
                BackupPath = files.Path.Combine(directory, "original" + extension), OldDestinationPath = song.DestinationPath,
                OldPreviousPath = song.PreviousDestinationPath, BeforeJson = before?.GetValueOrDefault(song.Id) ?? "",
                AfterJson = Snapshot(song), SourceSha256 = HashIfPresent(files, song.SourcePath) ?? "",
                DestinationBeforeSha256 = HashIfPresent(files, track.PlannedPath), UpdatedAtUtc = DateTime.UtcNow });
        }
        db.ReviewCopyJobs.Add(job);
        db.ReviewCopyRequestKeys.Add(new() { OwnerUserId = plan.OwnerUserId, RequestId = requestId,
            JobId = job.Id, PlanId = plan.Id, Revision = plan.Revision });
        return job;
    }

    internal static string? HashIfPresent(IFileSystem files, string path)
    {
        if (!files.File.Exists(path)) return null;
        using var stream = files.File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static async Task<ReviewCopyJobResult> GetAsync(MusicHoarderDbContext db, IFileSystem files,
        Guid owner, Guid id, CancellationToken ct, IReviewAudioCommandRunner? runner = null)
    {
        var job = await OwnedAsync(db, owner, id, ct);
        // Physical content is reobserved, never infer verification from Done or a persisted state.
        var fresh = job.State == "completed" && job.Tracks.All(t => t.State == "published" && t.ProofJson != null
            && t.CandidateSha256 == HashIfPresent(files, t.DestinationPath)
            && t.SourceSha256 == HashIfPresent(files, t.SourcePath));
        if (fresh && runner is not null)
        {
            var verifier = new ReviewAudioVerifier(runner, new ReviewAudioProofStore(db));
            var ids = job.Tracks.Select(t => t.SongId).ToArray();
            var songs = await db.Songs.AsNoTracking().Where(s => s.OwnerUserId == owner && ids.Contains(s.Id)).ToListAsync(ct);
            await ManualReviewMetadata.LoadAsync(db, songs, ct);
            foreach (var track in job.Tracks)
            {
                try
                {
                    var song = songs.SingleOrDefault(s => s.Id == track.SongId);
                    if (song is null || !Matches(song, track.AfterJson) || ReviewTagVerifier.Verify(track.DestinationPath, song).Count > 0
                        || !(await verifier.VerifyAsync(track.SourcePath, track.DestinationPath, ct)).Verified) fresh = false;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or TagLib.CorruptFileException
                    or TagLib.UnsupportedFormatException or ArgumentException) { fresh = false; }
            }
        }
        return new(job.Id, job.PlanId, job.RequestId, job.State, ReviewTimestamp.Persisted(job.CreatedAtUtc), ReviewTimestamp.Persisted(job.UpdatedAtUtc),
            job.RolledBackAtUtc is null, fresh, "notAssessed", job.Tracks.OrderBy(t => t.SongId).Select(t =>
                new ReviewCopyTrackResult(t.SongId, t.State, t.Attempts, t.FailureStage, t.FailureCode,
                    ReviewTimestamp.Persisted(t.UpdatedAtUtc), t.DestinationPath, t.ProofJson != null, t.ProofJson)).ToArray());
    }
    // Hosted retries have no HTTP user. The explicit owner remains the authorization boundary.
    internal static async Task<ReviewCopyJob> OwnedAsync(MusicHoarderDbContext db, Guid owner, Guid id, CancellationToken ct)
        => await db.ReviewCopyJobs.IgnoreQueryFilters().Include(j => j.Tracks)
            .SingleOrDefaultAsync(j => j.Id == id && j.OwnerUserId == owner, ct)
            ?? throw new ReviewPlanException(404, new("job_not_found", "Job unavailable to this owner."));

    internal static async Task RetryAsync(MusicHoarderDbContext db, Guid owner, Guid id, CancellationToken ct)
    {
        await using var guard = await ReviewWriteGuard.AcquireAsync(db, false, ct)
            ?? throw new ReviewPlanException(409, new("writer_busy", "A writer is active. Retry later."));
        db.ChangeTracker.Clear();
        var job = await OwnedAsync(db, owner, id, ct);
        if (job.State is "rolledBack" or "rollingBack") throw new ReviewPlanException(409, new("job_rolled_back", "This job was rolled back."));
        foreach (var track in job.Tracks) { track.FailureCode = null; track.FailureStage = null; }
        job.State = "pending"; job.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

internal sealed class ReviewAudioProofStore(MusicHoarderDbContext db) : IReviewAudioProofStore
{
    public async Task<ReviewAudioProof?> FindAsync(string version, string contentSha256, CancellationToken ct)
    {
        var entry = await db.ReviewAudioCache.AsNoTracking().SingleOrDefaultAsync(e => e.Version == version && e.ContentSha256 == contentSha256, ct);
        if (entry is null) return null;
        try { return JsonSerializer.Deserialize<ReviewAudioProof>(entry.ProofJson, ReviewCopyJobs.Json); }
        catch (JsonException) { return null; }
    }
    public async Task SaveAsync(ReviewAudioProof proof, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            var json = JsonSerializer.Serialize(proof, ReviewCopyJobs.Json);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ReviewAudioCache" ("Version", "ContentSha256", "ProofJson") VALUES ({proof.Version}, {proof.ContentSha256}, {json})
                ON CONFLICT ("Version", "ContentSha256") DO UPDATE SET "ProofJson" = EXCLUDED."ProofJson"
                """, ct);
            return;
        }
        var entry = await db.ReviewAudioCache.SingleOrDefaultAsync(e => e.Version == proof.Version && e.ContentSha256 == proof.ContentSha256, ct);
        if (entry is null) db.ReviewAudioCache.Add(new ReviewAudioCacheEntry { Version = proof.Version,
            ContentSha256 = proof.ContentSha256, ProofJson = JsonSerializer.Serialize(proof, ReviewCopyJobs.Json) });
        else entry.ProofJson = JsonSerializer.Serialize(proof, ReviewCopyJobs.Json);
        await db.SaveChangesAsync(ct);
    }
}
