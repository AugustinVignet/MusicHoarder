using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review.Copy;
using Npgsql;

namespace MusicHoarder.Api.Review.Audit;

internal interface ICopyAuditCheckpoints
{
    Task ReachedAsync(string stage, int songId, CancellationToken ct);
}

/// <summary>One physical track per gate acquisition. No musical writes or publication dependency.</summary>
internal sealed class CopyAuditProcessor(MusicHoarderDbContext db, IFileSystem files, IReviewAudioCommandRunner runner,
    IOptions<MusicEnricherOptions> options, ICopyAuditCheckpoints? checkpoints = null, IDirectoryAvailability? availability = null)
{
    internal const string Version = "copy-audit-v1:" + ReviewTagVerifier.Version + ":" + ReviewAudioVerifier.CodeVersion;
    internal TimeSpan TrackTimeout { get; init; } = TimeSpan.FromMinutes(15);

    internal async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var now = CopyAuditJobs.Now;
        if (!await db.CopyAuditJobs.AsNoTracking().AnyAsync(j => (j.State == "queued" || j.State == "running") &&
            !j.CancelRequested && (j.NextAttemptAtUtc == null || j.NextAttemptAtUtc <= now), ct)) return false;
        await using var gate = await ReviewWriteGuard.AcquireAsync(db, false, ct);
        if (gate is null)
        {
            await db.CopyAuditJobs.Where(j => (j.State == "queued" || j.State == "running") && !j.CancelRequested &&
                (j.NextAttemptAtUtc == null || j.NextAttemptAtUtc <= now)).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.WaitReason, "writer_busy").SetProperty(j => j.NextAttemptAtUtc, now.AddSeconds(5)), ct);
            return false;
        }
        db.ChangeTracker.Clear();
        var job = await db.CopyAuditJobs.AsNoTracking().Where(j => (j.State == "queued" || j.State == "running") &&
            !j.CancelRequested && (j.NextAttemptAtUtc == null || j.NextAttemptAtUtc <= now)).OrderBy(j => j.CreatedAtUtc).ThenBy(j => j.Id).FirstOrDefaultAsync(ct);
        if (job is null) return false;
        var track = await db.CopyAuditTracks.AsNoTracking().Where(t => t.JobId == job.Id &&
            (t.State == "queued" || t.State == "running" || t.State == "retryWaiting") && (t.NextAttemptAtUtc == null || t.NextAttemptAtUtc <= now))
            .OrderBy(t => t.SongId).FirstOrDefaultAsync(ct);
        if (track is null) { await FinishEmpty(job, gate, ct); return true; }
        await gate.CheckAsync(ct);
        var attempt = await Claim(job, track, ct);
        if (attempt is null) return true;
        using var control = CancellationTokenSource.CreateLinkedTokenSource(ct);
        control.CancelAfter(TrackTimeout);
        var monitor = Monitor(job.Id, gate, control);
        CopyAuditReceipt? receipt = null;
        try
        {
            receipt = await Check(job, track, attempt, control.Token);
            await Checkpoint("beforeReceipt", track.SongId, control.Token);
            // The last checkpoint is deliberately before every final freshness/hash check.
            receipt = await Recheck(job, track, receipt, control.Token);
            await gate.CheckAsync(control.Token);
            await Acknowledge(job, track, attempt, receipt, gate, control.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var canceled = await db.CopyAuditJobs.AsNoTracking().AnyAsync(j => j.Id == job.Id && j.CancelRequested, ct);
            receipt = EmptyReceipt(job, track, attempt, canceled ? "canceled" : "unverifiable", canceled ? "canceled" : "audit_timeout", "control");
            // A lost lock cannot write an acknowledgment: this call uses the original gate session.
            try { await Acknowledge(job, track, attempt, receipt, gate, ct); }
            catch (Exception e) when (e is NpgsqlException or IOException)
            { await DeferLostSession(job, track, attempt, receipt, ct); throw; }
        }
        catch (Exception e) when (e is NpgsqlException or IOException)
        {
            await DeferLostSession(job, track, attempt, receipt, ct);
            throw;
        }
        finally
        {
            control.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }
        return true;
    }

    private async Task DeferLostSession(CopyAuditJob job, CopyAuditTrack track, CopyAuditAttempt attempt,
        CopyAuditReceipt? previous, CancellationToken ct)
    {
        var now = CopyAuditJobs.Now; var next = now.AddSeconds(5);
        var receipt = (previous ?? EmptyReceipt(job, track, attempt, "unverifiable", null, "control")) with
        { State = "unverifiable", FailureCode = "storage_or_lease_lost", ContentVerified = false, FinishedAtUtc = now };
        receipt = receipt with { Stages = receipt.Stages.Append(new CopyAuditStage("lease", "unverifiable", "storage_or_lease_lost")).ToArray() };
        var json = JsonSerializer.Serialize(receipt, CopyAuditJobs.Json);
        try
        {
            // Only a negative CAS transition is allowed off the lost session. A peer's newer
            // claim wins; no old worker can acknowledge a success after reconnecting.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH deferred AS (
                  UPDATE "CopyAuditTracks" SET "State"='retryWaiting', "FailureCode"='storage_or_lease_lost',
                    "NextAttemptAtUtc"={next}, "UpdatedAtUtc"={now}, "ReceiptJson"={json}
                  WHERE "JobId"={job.Id} AND "SongId"={track.SongId} AND "ClaimId"={attempt.ClaimId} AND "State"='running'
                    AND EXISTS(SELECT 1 FROM "CopyAuditJobs" j WHERE j."Id"={job.Id} AND NOT j."CancelRequested") RETURNING "SongId"
                ), ended AS (
                  UPDATE "CopyAuditAttempts" SET "State"='interrupted', "FinishedAtUtc"={now}, "ReceiptJson"={json},
                    "BytesHashed"={receipt.BytesHashed}, "DecodeAttempts"={receipt.DecodeAttempts},
                    "CacheHits"={(receipt.SourceCacheHit ? 1 : 0) + (receipt.CopyCacheHit ? 1 : 0)}
                  WHERE "JobId"={job.Id} AND "SongId"={track.SongId} AND "ClaimId"={attempt.ClaimId}
                    AND EXISTS(SELECT 1 FROM deferred) RETURNING "SongId"
                )
                UPDATE "CopyAuditJobs" SET "WaitReason"='storage_or_lease_lost', "UpdatedAtUtc"={now},
                  "ScopeState"=CASE WHEN "ScopeState"='stale' THEN 'stale' ELSE 'notReobserved' END,
                  "NextAttemptAtUtc"=CASE WHEN EXISTS(SELECT 1 FROM "CopyAuditTracks" t WHERE t."JobId"={job.Id}
                    AND t."SongId"<>{track.SongId} AND t."State" IN ('queued','running')) THEN NULL ELSE {next} END
                WHERE "Id"={job.Id} AND NOT "CancelRequested" AND EXISTS(SELECT 1 FROM deferred)
                """, ct);
        }
        catch (Exception e) when (e is NpgsqlException or IOException or OperationCanceledException)
        { /* A database outage may prevent even the negative receipt. The persisted claim recovers after restart. */ }
    }

    private async Task<CopyAuditAttempt?> Claim(CopyAuditJob job, CopyAuditTrack track, CancellationToken ct)
    {
        var claim = Guid.NewGuid();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
            var currentJob = await db.CopyAuditJobs.SingleAsync(j => j.Id == job.Id, token);
            if (currentJob.CancelRequested) return null;
            var committedClaim = await db.CopyAuditAttempts.AsNoTracking().SingleOrDefaultAsync(a =>
                a.JobId == job.Id && a.SongId == track.SongId && a.ClaimId == claim, token);
            if (committedClaim is not null)
            {
                // Retry after an uncertain COMMIT response must not create a second logical attempt.
                track.Attempts = committedClaim.Number; track.ClaimId = claim;
                await tx.CommitAsync(token); return committedClaim;
            }
            var current = await db.CopyAuditTracks.SingleAsync(t => t.JobId == job.Id && t.SongId == track.SongId, token);
            var prior = await db.CopyAuditAttempts.Where(a => a.JobId == job.Id && a.SongId == track.SongId && a.State == "running").ToListAsync(token);
            foreach (var interrupted in prior) { interrupted.State = "interrupted"; interrupted.FinishedAtUtc = CopyAuditJobs.Now; }
            current.State = "running"; current.ClaimId = claim; current.Attempts++; current.UpdatedAtUtc = CopyAuditJobs.Now;
            current.NextAttemptAtUtc = null; currentJob.State = "running"; currentJob.WaitReason = null; currentJob.NextAttemptAtUtc = null; currentJob.UpdatedAtUtc = CopyAuditJobs.Now;
            var attempt = new CopyAuditAttempt { JobId = job.Id, SongId = track.SongId, Number = current.Attempts, ClaimId = claim, StartedAtUtc = CopyAuditJobs.Now };
            db.CopyAuditAttempts.Add(attempt); await db.SaveChangesAsync(token); await tx.CommitAsync(token);
            track.Attempts = current.Attempts; track.ClaimId = claim;
            return attempt;
        }, ct);
    }

    private async Task Monitor(Guid id, ReviewWriteGuard gate, CancellationTokenSource control)
    {
        try
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false, Timeout = 5, CommandTimeout = 5 }.ConnectionString);
            await connection.OpenAsync(control.Token);
            while (!control.IsCancellationRequested)
            {
                await Task.Delay(250, control.Token);
                await gate.CheckAsync(control.Token);
                await using var command = new NpgsqlCommand("SELECT \"CancelRequested\" FROM \"CopyAuditJobs\" WHERE \"Id\" = @id", connection);
                command.Parameters.AddWithValue("id", id);
                if (await command.ExecuteScalarAsync(control.Token) is not false) { control.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (control.IsCancellationRequested) { }
        catch { control.Cancel(); } // Lost database/gate means no positive observation.
    }

    private async Task<CopyAuditReceipt> Check(CopyAuditJob job, CopyAuditTrack track, CopyAuditAttempt attempt, CancellationToken ct)
    {
        var receipt = EmptyReceipt(job, track, attempt, "unverifiable", null, "paths");
        var stages = new List<CopyAuditStage>();
        var metrics = new ReviewAudioMetrics();
        var stage = "metadata";
        try
        {
            var song = await Song(job.OwnerUserId, track.SongId, ct);
            if (song is null || CopyAuditJobs.DbRevision(song) != track.ExpectedDbRevision) return receipt with { State = "stale", FailureCode = "database_changed" };
            receipt = receipt with { ObservedDbRevision = CopyAuditJobs.DbRevision(song) };
            if (string.IsNullOrEmpty(song.DestinationPath)) return receipt with { FailureCode = "not_built", Stages = [new("copy", "unverifiable", "not_built")] };
            stage = "source";
            var sourceRoot = CopyAuditPaths.SourceRoot(track.SourcePath, options.Value.SourceDirectory, options.Value.DownloadDirectory);
            CheckCachedAvailability(sourceRoot, false);
            var source = CopyAuditPaths.Check(track.SourcePath, sourceRoot);
            receipt = receipt with { Source = AlbumReviewContextReader.ObserveFile(files, track.SourcePath) };
            stage = "copy";
            CheckCachedAvailability(options.Value.DestinationDirectory, true);
            var copy = CopyAuditPaths.Check(track.DestinationPath!, options.Value.DestinationDirectory);
            receipt = receipt with { Copy = AlbumReviewContextReader.ObserveFile(files, track.DestinationPath) };
            stages.Add(new("paths", "passed"));
            stage = "tags";
            var differences = ReviewTagVerifier.Verify(copy, song);
            stages.Add(new("tags", differences.Count == 0 ? "passed" : "issues", differences.Count == 0 ? null : "tag_mismatch", differences));
            await Checkpoint("beforeAudio", track.SongId, ct);
            stage = "audio";
            var audio = new ReviewAudioVerifier(runner, new ReviewAudioProofStore(db), metrics);
            var result = await audio.VerifyAsync(source, copy, ct);
            var failure = differences.Count > 0 ? "tag_mismatch" : result.FailureCode;
            var state = result.Verified && differences.Count == 0 ? "verified" : result.FailedStage == "comparison" || differences.Count > 0 ? "issues" : "unverifiable";
            if (result.FailureCode?.StartsWith("file_changed", StringComparison.Ordinal) is true) state = "stale";
            stages.Add(new("audio:" + (result.FailedStage ?? "comparison"), result.Verified ? "passed" : state, result.FailureCode));
            return receipt with { State = state, FailureCode = failure, Stages = stages, SourceReadSha256 = result.SourceReadSha256, CopyReadSha256 = result.CandidateReadSha256, SourceProof = Proof(result.Source), CopyProof = Proof(result.Candidate),
                SourceCacheHit = result.SourceCacheHit, CopyCacheHit = result.CandidateCacheHit, BytesHashed = metrics.BytesHashed,
                DecodeAttempts = metrics.DecodeAttempts, ContentVerified = result.Verified };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CopyAuditCheckException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or ArgumentException)
        {
            var code = e is CopyAuditCheckException known ? known.Code : e is FileNotFoundException or DirectoryNotFoundException
                ? stage == "source" ? "source_missing" : stage == "copy" ? "copy_missing" : "file_missing"
                : e is UnauthorizedAccessException ? "access_denied" : e is TagLib.UnsupportedFormatException ? "unsupported_tag_format" : e is TagLib.CorruptFileException ? "corrupt_tags" : "file_io_error";
            if (code == "source_missing") receipt = receipt with { Source = receipt.Source with { Presence = "missing" } };
            if (code == "copy_missing") receipt = receipt with { Copy = receipt.Copy with { Presence = "missing" } };
            stages.Add(new(stage, "unverifiable", code));
            return receipt with { State = stage == "copy" && code == "copy_missing" ? "issues" : "unverifiable", FailureCode = code,
                Stages = stages, BytesHashed = metrics.BytesHashed, DecodeAttempts = metrics.DecodeAttempts };
        }
    }

    private async Task<CopyAuditReceipt> Recheck(CopyAuditJob job, CopyAuditTrack track, CopyAuditReceipt receipt, CancellationToken ct)
    {
        var song = await Song(job.OwnerUserId, track.SongId, ct);
        var dbRevision = song is null ? null : CopyAuditJobs.DbRevision(song);
        if (receipt.FailureCode is "root_unavailable" or "access_denied" or "file_io_error" or "file_unreadable")
        {
            // A negative result must not re-enter an unavailable mount through E's stats.
            return receipt with { State = dbRevision != track.ExpectedDbRevision ? "stale" : receipt.State, ContentVerified = false,
                ObservedDbRevision = dbRevision, ObservedContextRevision = null, FinishedAtUtc = CopyAuditJobs.Now,
                Stages = receipt.Stages.Append(new CopyAuditStage("freshness", "notReobserved", "storage_unavailable")).ToArray() };
        }
        var stale = dbRevision != track.ExpectedDbRevision;
        string? revision;
        try { revision = await ScopeRevision(job, JsonSerializer.Deserialize<AlbumReviewContextRequest>(job.ScopeJson, CopyAuditJobs.Json)!, ct); }
        catch (AlbumReviewReadException) { revision = null; }
        stale |= revision != job.ExpectedContextRevision;
        var observed = await ReobserveInputs(track, song, receipt, ct); stale |= observed.Changed; receipt = observed.Receipt;
        await Checkpoint("afterFinalHash", track.SongId, ct);
        observed = await ReobserveInputs(track, song, receipt, ct); stale |= observed.Changed; receipt = observed.Receipt;
        // The last DB snapshot also catches an enrichment/reference update during full hashing.
        song = await Song(job.OwnerUserId, track.SongId, ct);
        dbRevision = song is null ? null : CopyAuditJobs.DbRevision(song);
        try { revision = await ScopeRevision(job, JsonSerializer.Deserialize<AlbumReviewContextRequest>(job.ScopeJson, CopyAuditJobs.Json)!, ct); }
        catch (AlbumReviewReadException) { revision = null; }
        stale |= dbRevision != track.ExpectedDbRevision || revision != job.ExpectedContextRevision;
        return receipt with { State = stale ? "stale" : receipt.State, FailureCode = stale ? "scope_or_files_changed" : receipt.FailureCode,
            ContentVerified = !stale && receipt.ContentVerified, ObservedDbRevision = dbRevision, ObservedContextRevision = revision,
            FinishedAtUtc = CopyAuditJobs.Now,
            Stages = receipt.Stages.Append(new CopyAuditStage("freshness", stale ? "stale" : "passed", stale ? "scope_or_files_changed" : null)).ToArray() };
    }

    private async Task<(bool Changed, CopyAuditReceipt Receipt)> ReobserveInputs(CopyAuditTrack track, SongMetadata? song,
        CopyAuditReceipt receipt, CancellationToken ct)
    {
        var sourceHash = receipt.SourceReadSha256 ?? receipt.SourceProof?.ContentSha256;
        var copyHash = receipt.CopyReadSha256 ?? receipt.CopyProof?.ContentSha256;
        if (sourceHash is null && copyHash is null) return (false, receipt);
        var metrics = new ReviewAudioMetrics(); var changed = false;
        try
        {
            var audio = new ReviewAudioVerifier(runner, metrics: metrics);
            if (sourceHash is not null)
            {
                CheckCachedAvailability(CopyAuditPaths.SourceRoot(track.SourcePath, options.Value.SourceDirectory, options.Value.DownloadDirectory), false);
                var source = CopyAuditPaths.Check(track.SourcePath, CopyAuditPaths.SourceRoot(track.SourcePath, options.Value.SourceDirectory, options.Value.DownloadDirectory));
                changed |= (await audio.SnapshotAsync(source, ct)).Hash != sourceHash;
            }
            if (copyHash is not null)
            {
                CheckCachedAvailability(options.Value.DestinationDirectory, true);
                var copy = CopyAuditPaths.Check(track.DestinationPath!, options.Value.DestinationDirectory);
                changed |= (await audio.SnapshotAsync(copy, ct)).Hash != copyHash;
                var initial = receipt.Stages.SingleOrDefault(s => s.Stage == "tags")?.Fields ?? [];
                if (song is null || !initial.SequenceEqual(ReviewTagVerifier.Verify(copy, song))) changed = true;
            }
            receipt = receipt with { ContentObservedAtUtc = CopyAuditJobs.Now };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CopyAuditCheckException or ReviewAudioVerifier.AudioCheckException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        { changed = true; }
        return (changed, receipt with { BytesHashed = receipt.BytesHashed + metrics.BytesHashed });
    }

    private void CheckCachedAvailability(string root, bool copy)
    {
        // Read only: ProbeNowAsync can trigger discovery and must never be called by an audit.
        var status = availability?.Current;
        if (status is null || status.CheckedAtUtc == DateTime.MinValue ||
            CopyAuditJobs.Now - status.CheckedAtUtc > TimeSpan.FromSeconds(2 * options.Value.DirectoryProbeIntervalSeconds + 2 * options.Value.DirectoryProbeTimeoutSeconds)) return;
        var monitoredRoot = copy ? status.DestinationDirectory : status.SourceDirectory;
        if (string.IsNullOrWhiteSpace(monitoredRoot) || !Path.IsPathFullyQualified(monitoredRoot)) return;
        if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(monitoredRoot)) &&
            !(copy ? status.DestinationAvailable : status.SourceAvailable)) throw new CopyAuditCheckException("root_unavailable");
    }

    private async Task<string> ScopeRevision(CopyAuditJob job, AlbumReviewContextRequest scope, CancellationToken ct)
    {
        // Native hosted services have no HTTP user. Use a deliberately unscoped read context,
        // then the same explicit owner/active-row predicates as E; never invent a principal.
        await using var read = new MusicHoarderDbContext(new DbContextOptionsBuilder()
            .UseNpgsql(db.Database.GetConnectionString(), p => p.EnableRetryOnFailure()).Options);
        return await read.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var tx = await read.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
            await read.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
            var context = await AlbumAuditReader.ReadPlanAsync(read, files, job.OwnerUserId, scope, token);
            await tx.CommitAsync(token); return context.Revision;
        }, ct);
    }

    private async Task<SongMetadata?> Song(Guid owner, int id, CancellationToken ct)
    {
        var song = await AlbumReviewContextReader.Project(db.Songs.IgnoreQueryFilters().AsNoTracking().Where(s => s.OwnerUserId == owner && s.Id == id && s.DeletedAtUtc == null)).SingleOrDefaultAsync(ct);
        if (song is not null) await ManualReviewMetadata.LoadAsync(db, [song], ct);
        return song;
    }

    private static CopyAuditReceipt EmptyReceipt(CopyAuditJob job, CopyAuditTrack track, CopyAuditAttempt attempt,
        string state, string? code, string stage) => new(track.SongId, attempt.Number, state, code, track.ExpectedDbRevision, null,
            job.ExpectedContextRevision, null, attempt.StartedAtUtc, CopyAuditJobs.Now, Version,
            new(track.SourcePath, "notObserved", null, null), new(track.DestinationPath ?? "", "notObserved", null, null),
            [new(stage, state, code)], null, null, null, null, false, false, 0, 0, null, false);
    private static CopyAuditContentProof? Proof(ReviewAudioProof? p) => p is null ? null : new(p.Version, p.ContentSha256, p.SizeBytes,
        p.EncodedAudioSha256, p.DecodedAudioSha256, p.Streams.Select(s => new CopyAuditAudioStream(s.Codec, s.SampleRate, s.Channels)).ToArray(), ReviewTimestamp.Persisted(p.VerifiedAtUtc));
    private Task Checkpoint(string stage, int id, CancellationToken ct) => checkpoints?.ReachedAsync(stage, id, ct) ?? Task.CompletedTask;

    private async Task Acknowledge(CopyAuditJob job, CopyAuditTrack track, CopyAuditAttempt attempt,
        CopyAuditReceipt receipt, ReviewWriteGuard gate, CancellationToken ct)
    {
        await gate.InSessionAsync(async connection =>
        {
            // This short transaction commits on the gate-owning session. It cannot reconnect,
            // reacquire a lost lease implicitly, or acknowledge an obsolete claim.
            await using var tx = await connection.BeginTransactionAsync(ct);
            await using var read = new NpgsqlCommand("SELECT \"CancelRequested\" FROM \"CopyAuditJobs\" WHERE \"Id\" = @job FOR UPDATE", connection, tx);
            read.Parameters.AddWithValue("job", job.Id);
            var canceled = await read.ExecuteScalarAsync(ct) is true;
            await using var currentClaim = new NpgsqlCommand("SELECT \"ClaimId\" FROM \"CopyAuditTracks\" WHERE \"JobId\" = @job AND \"SongId\" = @song FOR UPDATE", connection, tx);
            currentClaim.Parameters.AddWithValue("job", job.Id); currentClaim.Parameters.AddWithValue("song", track.SongId);
            if (await currentClaim.ExecuteScalarAsync(ct) is not Guid claim || claim != attempt.ClaimId) return false;
            if (canceled) receipt = receipt with { State = "canceled", FailureCode = "canceled", ContentVerified = false };
            var transient = receipt.FailureCode is "access_denied" or "file_io_error" or "audio_tool_timeout" or "audio_tool_unavailable" or "audit_timeout" or "root_unavailable";
            var failures = track.TransientFailures + (transient ? 1 : 0);
            var state = transient && failures < 3 && !canceled ? "retryWaiting" : receipt.State;
            DateTime? next = state == "retryWaiting" ? CopyAuditJobs.Now.AddSeconds(5 * Math.Pow(4, failures - 1)) : null;
            await using var save = new NpgsqlCommand("""
                UPDATE "CopyAuditTracks" SET "State" = @state, "TransientFailures" = @failures,
                  "FailureCode" = @code, "NextAttemptAtUtc" = @next, "UpdatedAtUtc" = @now, "ReceiptJson" = @receipt
                WHERE "JobId" = @job AND "SongId" = @song AND "ClaimId" = @claim;
                UPDATE "CopyAuditAttempts" SET "State" = @result, "FinishedAtUtc" = @now, "ReceiptJson" = @receipt,
                  "BytesHashed" = @bytes, "DecodeAttempts" = @decodes, "CacheHits" = @hits
                WHERE "JobId" = @job AND "SongId" = @song AND "Number" = @number AND "ClaimId" = @claim;
                UPDATE "CopyAuditJobs" SET "UpdatedAtUtc" = @now, "ObservedContextRevision" = @revision,
                  "ScopeState" = CASE WHEN "ScopeState" = 'stale' OR @result = 'stale' THEN 'stale'
                    WHEN @revision IS NULL THEN 'notReobserved'
                    WHEN @revision = "ExpectedContextRevision" THEN 'observedCurrent' ELSE 'stale' END,
                  "State" = CASE WHEN "CancelRequested" THEN 'canceled'
                    WHEN EXISTS(SELECT 1 FROM "CopyAuditTracks" WHERE "JobId" = @job AND "State" IN ('queued','running','retryWaiting')) THEN 'running'
                    WHEN EXISTS(SELECT 1 FROM "CopyAuditTracks" WHERE "JobId" = @job AND "State" <> 'verified') OR @revision IS DISTINCT FROM "ExpectedContextRevision" THEN 'completedWithIssues'
                    ELSE 'completed' END,
                  "NextAttemptAtUtc" = CASE WHEN EXISTS(SELECT 1 FROM "CopyAuditTracks" WHERE "JobId" = @job AND "State" IN ('queued','running')) THEN NULL
                    ELSE (SELECT min("NextAttemptAtUtc") FROM "CopyAuditTracks" WHERE "JobId" = @job AND "State" = 'retryWaiting') END,
                  "WaitReason" = CASE WHEN @next IS NOT NULL THEN @code ELSE NULL END
                WHERE "Id" = @job
                """, connection, tx);
            void Add(string name, object? value, NpgsqlTypes.NpgsqlDbType type) => save.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
            Add("state", state, NpgsqlTypes.NpgsqlDbType.Text); Add("result", receipt.State, NpgsqlTypes.NpgsqlDbType.Text);
            Add("failures", failures, NpgsqlTypes.NpgsqlDbType.Integer); Add("code", receipt.FailureCode, NpgsqlTypes.NpgsqlDbType.Text);
            Add("next", next, NpgsqlTypes.NpgsqlDbType.TimestampTz); Add("now", CopyAuditJobs.Now, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            Add("receipt", JsonSerializer.Serialize(receipt, CopyAuditJobs.Json), NpgsqlTypes.NpgsqlDbType.Text);
            Add("job", job.Id, NpgsqlTypes.NpgsqlDbType.Uuid); Add("song", track.SongId, NpgsqlTypes.NpgsqlDbType.Integer);
            Add("claim", attempt.ClaimId, NpgsqlTypes.NpgsqlDbType.Uuid); Add("number", attempt.Number, NpgsqlTypes.NpgsqlDbType.Integer);
            Add("bytes", receipt.BytesHashed, NpgsqlTypes.NpgsqlDbType.Bigint); Add("decodes", receipt.DecodeAttempts, NpgsqlTypes.NpgsqlDbType.Integer);
            Add("hits", (receipt.SourceCacheHit ? 1 : 0) + (receipt.CopyCacheHit ? 1 : 0), NpgsqlTypes.NpgsqlDbType.Integer);
            Add("revision", receipt.ObservedContextRevision, NpgsqlTypes.NpgsqlDbType.Text);
            await save.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return true;
        }, ct);
    }

    private async Task FinishEmpty(CopyAuditJob job, ReviewWriteGuard gate, CancellationToken ct)
    {
        await gate.CheckAsync(ct);
        await db.CopyAuditJobs.Where(j => j.Id == job.Id && !j.CancelRequested).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.NextAttemptAtUtc, db.CopyAuditTracks.Where(t => t.JobId == job.Id && t.State == "retryWaiting").Min(t => t.NextAttemptAtUtc)), ct);
    }
}

