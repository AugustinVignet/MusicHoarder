using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Review.Retry;

internal interface ITargetedEnrichmentRunner
{
    Task<EnrichmentOutcome> RunAsync(int songId, bool fingerprintQualified, CancellationToken ct);
}
internal sealed class TargetedEnrichmentRunner(IEnrichmentOrchestrator orchestrator) : ITargetedEnrichmentRunner
{
    public Task<EnrichmentOutcome> RunAsync(int songId, bool fingerprintQualified, CancellationToken ct)
        => orchestrator is EnrichmentOrchestrator native ? native.ProcessTargetedSongAsync(songId, fingerprintQualified, ct)
            : throw new InvalidOperationException("Targeted enrichment requires the native scoped entry point.");
}
internal interface ITargetedRetryCheckpoints
{
    Task ReachedAsync(string stage, int songId, CancellationToken ct);
}
internal sealed class TargetedRetryProcessor(MusicHoarderDbContext db, IFileSystem files, IFpcalcService fpcalc,
    IReviewAudioCommandRunner tools, ITargetedEnrichmentRunner enrichment, ReviewCopyProcessor copies,
    ITargetedRetryCheckpoints? checkpoints = null)
{
    internal async Task<bool> RunNextAsync(CancellationToken ct)
    {
        await using var publisher = await ReviewWriteGuard.AcquireAsync(db, false, ct);
        if (publisher is null) return false;
        db.ChangeTracker.Clear();
        var job = await db.TargetedRetryJobs.IgnoreQueryFilters().Include(j => j.Items)
            .Where(j => j.State == "pending" || j.State == "running").OrderBy(j => j.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (job is null) return false;
        await using var guards = await ReviewWriteGuard.AcquireSongsAsync(db, job.Items.Select(i => i.SongId), ct);
        if (guards is null) return false;
        var request = JsonSerializer.Deserialize<TargetedRetryRequest>(job.RequestJson, ReviewCopyJobs.Json)!;
        job.State = "running"; await Save(job, ct);
        foreach (var group in job.Items.GroupBy(i => i.SongId).OrderBy(g => g.Key))
        {
            foreach (var item in group.OrderBy(i => i.Ordinal))
            {
                if (item.CompletedAtUtc is not null) continue;
                var song = await db.Songs.IgnoreQueryFilters().Include(s => s.ProviderAttempts)
                    .SingleOrDefaultAsync(s => s.Id == item.SongId && s.OwnerUserId == job.OwnerUserId, ct);
                if (song is null || song.IsDeleted || song.IsSynthetic)
                { Complete(item, "failed", "track_unavailable"); await Save(job, ct); continue; }
                if (!song.AutomationHeld) { Complete(item, "failed", "automation_ownership_changed"); await Save(job, ct); continue; }
                var previousFailed = group.Any(i => i.Ordinal < item.Ordinal && i.State is not ("success" or "skipped"));
                if (previousFailed) { Complete(item, "blocked", "prior_stage_not_success"); await Save(job, ct); continue; }
                var receipt = await db.PipelineStageAttempts.IgnoreQueryFilters().AsNoTracking()
                    .SingleOrDefaultAsync(a => a.JobId == job.Id && a.SongId == item.SongId && a.Stage == item.Stage, ct);
                if (receipt is not null)
                { Complete(item, receipt.State, receipt.Code); await Save(job, ct); continue; }
                item.State = "running"; item.StartedAtUtc ??= DateTime.UtcNow; await Save(job, ct);
                if (checkpoints is not null) await checkpoints.ReachedAsync("started:" + item.Stage, item.SongId, ct);
                try
                {
                    var hash = ReviewCopyJobs.HashIfPresent(files, song.SourcePath);
                    if (hash != item.InputSha256)
                    { await Record(job, item, song, "failed", "input_changed", hash, null, ct); continue; }
                    if (item.Stage == "fingerprint")
                    {
                        var prior = await db.PipelineStageStates.IgnoreQueryFilters().AsNoTracking()
                            .SingleOrDefaultAsync(s => s.SongId == song.Id && s.Stage == "fingerprint", ct);
                        if (!request.RecomputeSuccessful && FingerprintStage.FreshAccepted(prior, song, hash))
                        { Complete(item, "skipped", "already_valid"); await Save(job, ct); continue; }
                        var result = await FingerprintStage.ExecuteAsync(db, song, song.SourcePath, files, fpcalc, tools, job.Id, ct);
                        // The stage journal and fingerprint are already one database receipt. A crash
                        // here is recovered above without invoking fpcalc or a provider again.
                        if (checkpoints is not null) await checkpoints.ReachedAsync("recorded:fingerprint", song.Id, ct);
                        Complete(item, result.State, result.Code); await Save(job, ct);
                    }
                    else if (item.Stage == "enrichment")
                    {
                        if (song.IsManuallyApproved || song.ReviewPlanId.HasValue)
                        { Complete(item, "skipped", "manual_approval_preserved"); await Save(job, ct); continue; }
                        if (!request.RespectRetryAfter)
                        {
                            foreach (var attempt in song.ProviderAttempts.Where(a => a.Status != ProviderAttemptStatus.Matched))
                            { attempt.NextRetryAfterUtc = DateTime.UtcNow; attempt.RetryAfterUtc = DateTime.UtcNow; }
                            await db.SaveChangesAsync(ct);
                        }
                        var retryAt = song.ProviderAttempts.Where(a => a.Status != ProviderAttemptStatus.Matched)
                            .Select(a => a.RetryAfterUtc ?? a.NextRetryAfterUtc).Where(t => t.HasValue).Min();
                        if (request.RespectRetryAfter && retryAt > DateTime.UtcNow && song.ProviderAttempts.All(a => a.Status == ProviderAttemptStatus.Matched
                            || (a.RetryAfterUtc ?? a.NextRetryAfterUtc) > DateTime.UtcNow))
                        { await Record(job, item, song, "deferred", "provider_retry_after", hash, retryAt, ct); continue; }
                        var fingerprint = await db.PipelineStageStates.IgnoreQueryFilters().AsNoTracking()
                            .SingleOrDefaultAsync(s => s.SongId == song.Id && s.Stage == "fingerprint", ct);
                        var qualified = FingerprintStage.FreshAccepted(fingerprint, song, hash);
                        var outcome = await enrichment.RunAsync(song.Id, qualified, ct);
                        await db.Entry(song).ReloadAsync(ct);
                        var state = outcome == EnrichmentOutcome.Matched ? "success" : outcome == EnrichmentOutcome.NeedsReview ? "reviewRequired"
                            : outcome == EnrichmentOutcome.Skipped ? "skipped" : "failed";
                        await Record(job, item, song, state, outcome == EnrichmentOutcome.Failed ? "enrichment_failed" : null, hash, null, ct);
                    }
                    else
                    {
                        var result = await copies.RunSelectedAsync(job.OwnerUserId, request.CopyJobId!.Value, new HashSet<int> { song.Id }, publisher, ct);
                        var outcome = result[song.Id];
                        await Record(job, item, song, outcome.State, outcome.Code, hash, null, ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (ReviewPlanException e) { await Record(job, item, song, "failed", e.Error.Code, item.InputSha256, null, ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { await Record(job, item, song, "failed", "source_unreadable", item.InputSha256, null, ct); }
                catch (Exception e) when (e is not DbUpdateException)
                { await Record(job, item, song, "failed", "stage_exception", item.InputSha256, null, ct); }
            }
        }
        job.State = job.Items.All(i => i.State is "success" or "skipped") ? "completed" : "needsAttention";
        await publisher.CheckAsync(ct); await guards.CheckAsync(ct); await Save(job, ct);
        return true;
    }
    private async Task Record(TargetedRetryJob job, TargetedRetryItem item, SongMetadata song, string state, string? code,
        string? hash, DateTime? retryAt, CancellationToken ct)
    {
        Complete(item, state, code);
        await PipelineStageJournal.RecordAsync(db, song, item.Stage, state, code, hash, null, job.Id, null, retryAt, false, ct);
        await Save(job, ct);
    }
    private static void Complete(TargetedRetryItem item, string state, string? code)
    { item.State = state; item.Code = code; item.CompletedAtUtc = DateTime.UtcNow; }
    private async Task Save(TargetedRetryJob job, CancellationToken ct)
    { job.UpdatedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
}
