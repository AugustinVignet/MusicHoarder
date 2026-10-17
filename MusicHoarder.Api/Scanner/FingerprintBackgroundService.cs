using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Review.Retry;
using System.IO.Abstractions;

namespace MusicHoarder.Api.Scanner;

public class FingerprintBackgroundService(
    IServiceScopeFactory scopeFactory,
    JobManager jobManager,
    FingerprintProgressTracker progressTracker,
    IFpcalcService fpcalcService,
    IDuplicateDetectionService duplicateDetectionService,
    EnrichmentPipelineChannel enrichmentChannel,
    IDirectoryAvailability directoryAvailability,
    IOptions<MusicEnricherOptions> options,
    ILogger<FingerprintBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation(
            "Fingerprint background service started. Concurrency={Concurrency}, BatchSize={BatchSize}",
            opts.FingerprintConcurrency,
            opts.FingerprintBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid jobId;
            CancellationToken jobToken;
            int pendingCount;

            // Fingerprinting reads the source files via fpcalc; if the source is offline we'd
            // just mark every track permanently-failed. Idle-wait until it's reachable again.
            var sourceAvailable = directoryAvailability.Current.SourceAvailable;

            if (jobManager.FingerprintTriggers.TryRead(out var manualJobId))
            {
                jobId = manualJobId;
                jobToken = jobManager.GetCurrentCancellationToken();
                if (!sourceAvailable)
                {
                    logger.LogWarning("Fingerprint {JobId} skipped — source directory is offline", jobId);
                    jobManager.SignalComplete(jobId, cancelled: true);
                    continue;
                }
                pendingCount = await CountPendingAsync(stoppingToken);
            }
            else
            {
                if (!sourceAvailable)
                {
                    if (!await DelayIdleAsync(opts.FingerprintIdleDelaySeconds, stoppingToken)) break;
                    continue;
                }

                // Manual mode: don't auto-discover pending work — wait for an explicit trigger.
                if (!opts.AutoStartPipeline)
                {
                    var manualTrigger = jobManager.FingerprintTriggers.WaitToReadAsync(stoppingToken).AsTask();
                    var manualDelay = Task.Delay(TimeSpan.FromSeconds(opts.FingerprintIdleDelaySeconds), stoppingToken);
                    await Task.WhenAny(manualTrigger, manualDelay);
                    continue;
                }

                pendingCount = await CountPendingAsync(stoppingToken);

                if (pendingCount == 0)
                {
                    var triggerTask = jobManager.FingerprintTriggers.WaitToReadAsync(stoppingToken).AsTask();
                    var delayTask = Task.Delay(TimeSpan.FromSeconds(opts.FingerprintIdleDelaySeconds), stoppingToken);
                    await Task.WhenAny(triggerTask, delayTask);
                    continue;
                }

                jobId = Guid.NewGuid();
                if (!jobManager.TryRegisterAutoJob(JobType.Fingerprint, jobId, out jobToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(opts.FingerprintIdleDelaySeconds), stoppingToken);
                    continue;
                }
            }

            var runStarted = false;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
            var ct = linkedCts.Token;

            try
            {
                progressTracker.StartRun(jobId, pendingCount);
                runStarted = true;

                logger.LogInformation(
                    "Starting fingerprint run {RunId} with {PendingCount} pending tracks",
                    jobId, pendingCount);

                while (!ct.IsCancellationRequested)
                {
                    var processed = await ProcessNextBatchAsync(jobId, ct);
                    if (processed == 0) break;
                }

                progressTracker.CompleteRun(jobId);
                var wasCancelled = ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested;

                if (!wasCancelled)
                {
                    try
                    {
                        logger.LogInformation("Running duplicate detection after fingerprint run {RunId}", jobId);
                        await duplicateDetectionService.DetectDuplicatesAsync(stoppingToken);
                    }
                    catch (Exception dedupEx) when (dedupEx is not OperationCanceledException)
                    {
                        logger.LogWarning(dedupEx, "Duplicate detection failed after fingerprint run {RunId}", jobId);
                    }
                }

                jobManager.SignalComplete(jobId, wasCancelled);

                if (wasCancelled)
                    logger.LogInformation("Fingerprint run {RunId} cancelled via cancel endpoint", jobId);
                else
                    logger.LogInformation("Fingerprint run {RunId} complete", jobId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (runStarted) progressTracker.CompleteRun(jobId);
                jobManager.SignalComplete(jobId, cancelled: true);
                logger.LogInformation("Fingerprint run {RunId} stopped with application", jobId);
            }
            catch (Exception ex)
            {
                if (runStarted) progressTracker.CompleteRun(jobId);
                jobManager.SignalFailed(jobId);
                logger.LogError(ex, "Fingerprint run {RunId} failed", jobId);
                await Task.Delay(TimeSpan.FromSeconds(opts.FingerprintIdleDelaySeconds), stoppingToken);
            }
        }
    }

    private async Task<int> ProcessNextBatchAsync(Guid runId, CancellationToken ct)
    {
        var opts = options.Value;

        // AudioPath is the file fpcalc reads: the source, or the destination copy once the staged
        // source was released. SourcePath stays alongside because the priority lanes key on it.
        List<(int Id, string SourcePath, string? AudioPath)> batch;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            // Background service: bypass the per-user query filter. Skip synthetic (demo) rows
            // because they have no real file to fingerprint.
            var query = db.Songs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.AutomationHeld)
                .Where(s => s.Fingerprint == null || s.Fingerprint == string.Empty);

            query = query.Where(s => !db.PipelineStageStates.IgnoreQueryFilters().Any(p => p.SongId == s.Id && p.Stage == "fingerprint"
                && (p.Targeted || p.State == "failed" || p.State == "qualificationRequired")));

            // Explicitly-acquired tracks (the download staging root) jump the queue: a fresh import
            // must not fingerprint behind a whole-library backlog. Ordering by Id alone put new
            // downloads LAST — they have the highest ids by definition.
            var downloadPrefix = DownloadRootPrefix(opts);
            var ordered = downloadPrefix is null
                ? query.OrderBy(s => s.Id)
                : query.OrderByDescending(s => s.SourcePath.StartsWith(downloadPrefix)).ThenBy(s => s.Id);

            batch = await ordered
                .Take(opts.FingerprintBatchSize)
                .Select(s => new { s.Id, s.SourcePath, s.DestinationPath, s.PreviousDestinationPath, s.SourceReleasedAtUtc })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result
                    .Select(s => (s.Id, s.SourcePath, s.SourceReleasedAtUtc != null
                        ? s.DestinationPath ?? s.PreviousDestinationPath
                        : s.SourcePath))
                    .ToList(), ct);
        }

        if (batch.Count == 0) return 0;

        var results = new List<(int Id, FpcalcOutcome Outcome)>();
        var resultsLock = new object();

        // MaxDegreeOfParallelism already caps in-flight fpcalc invocations to FingerprintConcurrency,
        // so no additional SemaphoreSlim gate is needed.
        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = opts.FingerprintConcurrency,
                CancellationToken = ct
            },
            async (item, token) =>
            {
                using var trackScope = scopeFactory.CreateScope();
                var trackDb = trackScope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
                await using var gate = await ReviewWriteGuard.AcquireSongAsync(trackDb, item.Id, true, token);
                var song = await trackDb.Songs.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.Id == item.Id, token);
                if (song is null || song.IsDeleted || song.AutomationHeld || !string.IsNullOrEmpty(song.Fingerprint)) return;
                var files = trackScope.ServiceProvider.GetRequiredService<IFileSystem>();
                var tools = trackScope.ServiceProvider.GetRequiredService<IReviewAudioCommandRunner>();
                var audioPath = song.IsSourceReleased ? song.DestinationPath ?? song.PreviousDestinationPath : song.SourcePath;
                var observed = await FingerprintStage.ExecuteAsync(trackDb, song, audioPath, files, fpcalcService, tools, null, token);
                var outcome = observed.Accepted is { } accepted ? FpcalcOutcome.Success(accepted) : FpcalcOutcome.Failure(observed.Code ?? "fingerprint_failed");
                lock (resultsLock)
                {
                    results.Add((item.Id, outcome));
                }
            });

        foreach (var (id, outcome) in results)
        {
            if (outcome.Result is not null) progressTracker.IncrementFingerprinted();
            else { progressTracker.IncrementFailed(); logger.LogWarning("Fingerprint failed for song {Id}: {Code}", id, outcome.FailureReason); }
        }

        // Download-staged songs take the enrichment fast lane too — same reasoning as the batch
        // ordering above: an explicit acquisition should not enrich behind a backfill sweep.
        var pathById = batch.ToDictionary(b => b.Id, b => b.SourcePath);
        var enqueuePrefix = DownloadRootPrefix(opts);
        var fingerprintedIds = results
            .Where(r => r.Outcome.Result is not null)
            .Select(r => r.Id)
            .ToList();
        if (fingerprintedIds.Count > 0)
        {
            var priorityIds = enqueuePrefix is null
                ? []
                : fingerprintedIds.Where(id => pathById[id].StartsWith(enqueuePrefix, StringComparison.Ordinal)).ToList();
            enrichmentChannel.EnqueueRangePriority(priorityIds);
            enrichmentChannel.EnqueueRange(fingerprintedIds.Except(priorityIds));
            logger.LogDebug(
                "Enqueued {Count} fingerprinted songs for enrichment ({Priority} priority)",
                fingerprintedIds.Count, priorityIds.Count);
        }

        var state = progressTracker.GetCurrent();
        if (state is { Processed: var processed })
        {
            logger.LogInformation(
                "Fingerprint {RunId}: {Processed}/{Total} processed",
                runId, processed, state.TotalTracks);
        }

        return batch.Count;
    }

    /// <summary>Idle delay that returns false if the app is shutting down.</summary>
    /// <summary>
    /// The download staging root as a <see cref="SongMetadata.SourcePath"/> prefix (forward
    /// slashes, trailing separator so "…/downloads-other" never matches), or null when unset.
    /// </summary>
    internal static string? DownloadRootPrefix(MusicEnricherOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.DownloadDirectory))
            return null;
        var normalized = opts.DownloadDirectory.Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? null : normalized + "/";
    }

    private static async Task<bool> DelayIdleAsync(int seconds, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<int> CountPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var query = db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.AutomationHeld)
            .Where(s => s.Fingerprint == null || s.Fingerprint == string.Empty);

        query = query.Where(s => !db.PipelineStageStates.IgnoreQueryFilters().Any(p => p.SongId == s.Id && p.Stage == "fingerprint"
            && (p.Targeted || p.State == "failed" || p.State == "qualificationRequired")));

        return await query.CountAsync(ct);
    }
}
