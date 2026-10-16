using System.Data;
using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review.Copy;

namespace MusicHoarder.Api.Review.Retry;

internal static class TargetedRetryService
{
    private static readonly string[] Stages = ["fingerprint", "enrichment", "copies"];
    internal static async Task<TargetedRetryResult> CreateAsync(MusicHoarderDbContext db, IFileSystem files,
        Guid owner, TargetedRetryRequest request, CancellationToken ct)
    {
        Validate(request);
        request = request with { SongIds = request.SongIds.Order().ToArray() };
        var body = JsonSerializer.Serialize(request, ReviewCopyJobs.Json);
        var id = Guid.NewGuid();
        await using var publisher = await ReviewWriteGuard.AcquireAsync(db, false, ct) ?? throw Busy();
        await using var songsGuard = await ReviewWriteGuard.AcquireSongsAsync(db, request.SongIds, ct) ?? throw Busy();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var existing = await db.TargetedRetryJobs.Include(j => j.Items).SingleOrDefaultAsync(j => j.OwnerUserId == owner && j.RequestId == request.RequestId, token);
            if (existing is not null)
            {
                if (existing.RequestJson != body) throw Error(409, "request_key_reused", "This request UUID belongs to other retry content.");
                return Result(existing);
            }
            var songs = await db.Songs.Where(s => s.OwnerUserId == owner && s.DeletedAtUtc == null && !s.IsSynthetic && request.SongIds.Contains(s.Id)).ToListAsync(token);
            if (songs.Count != request.SongIds.Length) throw Error(404, "selection_unavailable", "Every selected track must be a real live track owned by this user.");
            if (request.CopyJobId is { } copyId)
            {
                var copy = await ReviewCopyJobs.OwnedAsync(db, owner, copyId, token);
                if (copy.State is not ("needsAttention" or "completed")) throw Error(409, "copy_job_busy", "Select an existing stopped copy job.");
                if (request.SongIds.Any(songId => !copy.Tracks.Any(t => t.SongId == songId)))
                    throw Error(400, "outside_copy_job", "Every selected track must belong to this copy job.");
            }
            var job = new TargetedRetryJob { Id = id, OwnerUserId = owner, RequestId = request.RequestId,
                RequestJson = body, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
            foreach (var song in songs)
            {
                // This is an automation fence, not a metadata reset or a change to duplicate decisions.
                song.AutomationHeld = true;
                string? hash;
                try { hash = ReviewCopyJobs.HashIfPresent(files, song.SourcePath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { hash = null; }
                for (var n = 0; n < request.Stages.Length; n++) job.Items.Add(new() { JobId = id, SongId = song.Id,
                    Ordinal = n, Stage = request.Stages[n], InputSha256 = hash });
            }
            db.TargetedRetryJobs.Add(job);
            await publisher.CheckAsync(token); await songsGuard.CheckAsync(token);
            await db.SaveChangesAsync(token); await tx.CommitAsync(token);
            return Result(job);
        }, ct);
    }
    internal static async Task<TargetedRetryResult> GetAsync(MusicHoarderDbContext db, Guid owner, Guid id, CancellationToken ct)
        => Result(await db.TargetedRetryJobs.AsNoTracking().Include(j => j.Items).SingleOrDefaultAsync(j => j.OwnerUserId == owner && j.Id == id, ct)
            ?? throw Error(404, "retry_not_found", "Retry unavailable to this owner."));
    internal static TargetedRetryResult Result(TargetedRetryJob job) => new(job.Id, job.RequestId, job.State,
        job.CreatedAtUtc, job.UpdatedAtUtc, job.Items.OrderBy(i => i.SongId).ThenBy(i => i.Ordinal)
            .Select(i => new TargetedRetryItemResult(i.SongId, i.Stage, i.State, i.Code, i.InputSha256, i.StartedAtUtc, i.CompletedAtUtc)).ToArray());

    internal static async Task<PipelineFailuresPage> FailuresAsync(MusicHoarderDbContext db, Guid owner, string? stage,
        int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 100 || stage is not null && !Stages.Contains(stage)) throw Error(400, "invalid_page", "Use a supported stage and offset >= 0, limit 1..100.");
        var rows = await db.PipelineStageStates.AsNoTracking().Where(s => s.OwnerUserId == owner
            && (stage == null || s.Stage == stage) && (s.State != "success" && s.State != "skipped" || s.SourceWarningCode != null))
            .Where(s => db.Songs.Any(song => song.Id == s.SongId && song.OwnerUserId == owner && song.DeletedAtUtc == null))
            .OrderBy(s => s.SongId).ThenBy(s => s.Stage).Skip(offset).Take(limit + 1).ToArrayAsync(ct);
        return new(offset, limit, rows.Length > limit, rows.Take(limit).Select(StateResult).ToArray());
    }
    internal static async Task<IReadOnlyList<PipelineStageResult>> StagesAsync(MusicHoarderDbContext db, Guid owner, int songId, CancellationToken ct)
    {
        await RequireSong(db, owner, songId, ct);
        return (await db.PipelineStageStates.AsNoTracking().Where(s => s.OwnerUserId == owner && s.SongId == songId).OrderBy(s => s.Stage).ToArrayAsync(ct)).Select(StateResult).ToArray();
    }
    internal static async Task<IReadOnlyList<PipelineAttemptResult>> AttemptsAsync(MusicHoarderDbContext db, Guid owner,
        int songId, string? stage, int offset, int limit, CancellationToken ct)
    {
        await RequireSong(db, owner, songId, ct);
        if (offset < 0 || limit is < 1 or > 100 || stage is not null && !Stages.Contains(stage)) throw Error(400, "invalid_page", "Use a supported stage and offset >= 0, limit 1..100.");
        return await db.PipelineStageAttempts.AsNoTracking().Where(a => a.OwnerUserId == owner && a.SongId == songId && (stage == null || a.Stage == stage))
            .OrderByDescending(a => a.AttemptedAtUtc).ThenBy(a => a.Id).Skip(offset).Take(limit)
            .Select(a => new PipelineAttemptResult(a.Id, a.SongId, a.JobId, a.Stage, a.State, a.Code, a.ExitCode,
                a.InputSha256, a.CandidateJson, a.AttemptedAtUtc, a.NextRetryAfterUtc)).ToArrayAsync(ct);
    }
    private static PipelineStageResult StateResult(PipelineStageState s) => new(s.SongId, s.Stage, s.State, s.Code, s.Attempts,
        s.UpdatedAtUtc, s.NextRetryAfterUtc, s.InputSha256, s.CandidateJson, s.SourceWarningCode, s.SourceWarningInputSha256);
    private static async Task RequireSong(MusicHoarderDbContext db, Guid owner, int songId, CancellationToken ct)
    { if (!await db.Songs.AnyAsync(s => s.Id == songId && s.OwnerUserId == owner && s.DeletedAtUtc == null, ct)) throw Error(404, "track_unavailable", "Track unavailable to this owner."); }
    internal static async Task ResumeAutomationAsync(MusicHoarderDbContext db, IFileSystem files, Guid owner,
        ResumeAutomationRequest request, CancellationToken ct)
    {
        if (request.SongIds is null || request.SongIds.Length is < 1 or > 200 || request.SongIds.Any(i => i <= 0)
            || request.SongIds.Distinct().Count() != request.SongIds.Length
            || !request.AllowProviders || !request.AllowBuild || !request.AllowDuplicateDetection)
            throw Error(400, "explicit_effects_required", "Resuming automation requires distinct IDs and explicit permission for providers, builds and duplicate detection.");
        await using var publisher = await ReviewWriteGuard.AcquireAsync(db, false, ct) ?? throw Busy();
        await using var guards = await ReviewWriteGuard.AcquireSongsAsync(db, request.SongIds, ct) ?? throw Busy();
        db.ChangeTracker.Clear();
        var songs = await db.Songs.Where(s => s.OwnerUserId == owner && s.DeletedAtUtc == null && request.SongIds.Contains(s.Id)).ToListAsync(ct);
        if (songs.Count != request.SongIds.Length) throw Error(404, "selection_unavailable", "Selection unavailable to this owner.");
        if (await db.TargetedRetryJobs.Where(j => j.OwnerUserId == owner && (j.State == "pending" || j.State == "running"))
            .AnyAsync(j => j.Items.Any(i => request.SongIds.Contains(i.SongId)), ct))
            throw Error(409, "retry_in_progress", "Selected work is not finished.");
        var states = await db.PipelineStageStates.Where(s => s.OwnerUserId == owner && request.SongIds.Contains(s.SongId) && s.Stage == "fingerprint").ToArrayAsync(ct);
        foreach (var song in songs)
        {
            var proof = states.SingleOrDefault(s => s.SongId == song.Id);
            if (!FingerprintStage.FreshAccepted(proof, song, ReviewCopyJobs.HashIfPresent(files, song.SourcePath)))
                throw Error(409, "qualification_required", "Every selected fingerprint needs fresh successful evidence without unresolved source warnings.");
        }
        foreach (var song in songs) song.AutomationHeld = false;
        await publisher.CheckAsync(ct); await guards.CheckAsync(ct); await db.SaveChangesAsync(ct);
    }

    internal static void Validate(TargetedRetryRequest request)
    {
        if (request.RequestId == Guid.Empty || request.SongIds is null || request.SongIds.Length is < 1 or > 200
            || request.SongIds.Any(i => i <= 0) || request.SongIds.Distinct().Count() != request.SongIds.Length
            || request.Stages is null || request.Stages.Length is < 1 or > 3 || request.Stages.Any(s => !Stages.Contains(s))
            || request.Stages.Distinct().Count() != request.Stages.Length
            || !request.Stages.Select(s => Array.IndexOf(Stages, s)).SequenceEqual(request.Stages.Select(s => Array.IndexOf(Stages, s)).Order())
            || request.AllowProviders != request.Stages.Contains("enrichment")
            || request.Stages.Contains("copies") != request.CopyJobId.HasValue)
            throw Error(400, "invalid_targeted_retry", "Supply a nonempty request UUID, 1..200 distinct IDs, and ordered explicit stages fingerprint/enrichment/copies. Enrichment requires allowProviders; copies requires copyJobId. No scan/build/dedup side effects are inferred.");
    }
    internal static ReviewPlanException Error(int status, string code, string message) => new(status, new(code, message));
    internal static ReviewPlanException Busy() => Error(409, "writer_busy", "A selected track or library writer is active. Retry later; nothing was queued.");
}
