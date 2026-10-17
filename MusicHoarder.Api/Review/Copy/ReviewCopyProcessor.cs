using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review.Copy;

internal interface IReviewCopyCheckpoints
{
    Task ReachedAsync(string stage, ReviewCopyTrack track, CancellationToken ct);
}
internal sealed class ReviewCopyFailure(string stage, string code) : Exception(code)
{
    internal string Stage { get; } = stage;
    internal string Code { get; } = code;
}

/// <summary>Target-only durable worker. Every file transition has a persisted intent, immutable
/// hashes and a recoverable old copy. PostgreSQL and the filesystem are separate commit domains.</summary>
internal sealed class ReviewCopyProcessor(MusicHoarderDbContext db, IFileSystem files,
    ILibraryTagWriter writer, IDestinationPathResolver resolver, IReviewAudioCommandRunner runner,
    IReviewCopyCheckpoints? checkpoints = null)
{
    private readonly ReviewAudioVerifier audio = new(runner, new ReviewAudioProofStore(db));

    internal async Task<bool> RunNextAsync(CancellationToken ct)
    {
        await using var guard = await ReviewWriteGuard.AcquireAsync(db, false, ct);
        if (guard is null) return false;
        db.ChangeTracker.Clear();
        var job = await db.ReviewCopyJobs.IgnoreQueryFilters().Include(j => j.Tracks)
            .Where(j => j.State == "pending" || j.State == "running" || j.State == "rollingBack")
            .OrderBy(j => j.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (job is null) return false;
        if (job.State == "rollingBack")
        {
            await ReviewCopyRollback.RunAsync(db, files, job.OwnerUserId, job.Id, ct, guard);
            return true;
        }
        job.State = "running"; await Save(job, ct);
        foreach (var track in job.Tracks.OrderBy(t => t.SongId))
        {
            try { await Process(job, track, guard, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is ReviewCopyFailure or IOException or UnauthorizedAccessException
                or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or ArgumentException)
            {
                track.State = "failed";
                track.FailureStage = e is ReviewCopyFailure failure ? failure.Stage : "filesystem";
                track.FailureCode = e is ReviewCopyFailure known ? known.Code : e is UnauthorizedAccessException ? "access_denied" : "copy_io_error";
                track.UpdatedAtUtc = DateTime.UtcNow;
                await Save(job, ct);
            }
        }
        job.State = job.Tracks.All(t => t.State == "published") ? "completed" : "needsAttention";
        await Save(job, ct);
        return true;
    }

    // D resumes an explicit subset of an already-stopped job. Do not mark it running:
    // after a crash the durable D intent, not the generic all-track C poller, owns this subset.
    internal async Task<IReadOnlyDictionary<int, (string State, string? Code)>> RunSelectedAsync(Guid owner, Guid id,
        IReadOnlySet<int> ids, ReviewWriteGuard guard, CancellationToken ct)
    {
        var job = await ReviewCopyJobs.OwnedAsync(db, owner, id, ct);
        if (job.State is not ("needsAttention" or "completed"))
            throw TargetedRetryServiceError();
        var result = new Dictionary<int, (string State, string? Code)>();
        foreach (var track in job.Tracks.Where(t => ids.Contains(t.SongId)).OrderBy(t => t.SongId))
        {
            try { await Process(job, track, guard, ct); result[track.SongId] = ("success", null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is ReviewCopyFailure or IOException or UnauthorizedAccessException
                or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or ArgumentException)
            {
                track.State = "failed"; track.FailureStage = e is ReviewCopyFailure failure ? failure.Stage : "filesystem";
                track.FailureCode = e is ReviewCopyFailure known ? known.Code : "copy_io_error";
                track.UpdatedAtUtc = DateTime.UtcNow; await Save(job, ct);
                result[track.SongId] = ("failed", track.FailureCode);
            }
        }
        job.State = job.Tracks.All(t => t.State == "published") ? "completed" : "needsAttention";
        await Save(job, ct);
        return result;
        static ReviewPlanException TargetedRetryServiceError() => new(409, new("copy_job_busy", "Targeted retry requires a stopped copy job."));
    }

    private async Task Process(ReviewCopyJob job, ReviewCopyTrack track, ReviewWriteGuard guard, CancellationToken ct)
    {
        var song = await db.Songs.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.Id == track.SongId && s.OwnerUserId == job.OwnerUserId, ct);
        if (song is null) Fail("metadata", "track_unavailable");
        await ManualReviewMetadata.LoadAsync(db, [song!], ct);
        if (!ReviewCopyJobs.Matches(song!, track.AfterJson)) Fail("metadata", "plan_superseded");
        if (resolver.ResolvePath(song!) != track.DestinationPath) Fail("metadata", "destination_policy_changed");
        if (string.IsNullOrEmpty(track.SourceSha256) || Hash(track.SourcePath) != track.SourceSha256)
            Fail("source", "source_changed_or_missing");
        // A recovered rename is verified again, never copied/tagged for a second time.
        if (track.State == "published" || track.CandidateSha256 is not null && Hash(track.DestinationPath) == track.CandidateSha256)
        {
            if (track.CandidateSha256 is null || Hash(track.DestinationPath) != track.CandidateSha256)
                Fail("publication", "published_content_changed");
            await Verify(track, song!, track.DestinationPath, ct);
            await Finish(job, track, song!, ct);
            return;
        }
        track.Attempts++;
        track.State = "staging"; track.FailureCode = null; track.FailureStage = null;
        track.UpdatedAtUtc = DateTime.UtcNow; await Save(job, ct);
        await Checkpoint("beforeCopy", track, ct);
        files.Directory.CreateDirectory(files.Path.GetDirectoryName(track.StagingPath)!);
        // Only this job's deterministic candidate is ever replaced on a pre-publication retry.
        await using (var input = files.FileStream.New(track.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = files.FileStream.New(track.StagingPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, ct); await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
        }
        track.State = "copied"; track.UpdatedAtUtc = DateTime.UtcNow; await Save(job, ct);
        await Checkpoint("copied", track, ct);
        await writer.WriteTagsAsync(track.StagingPath, song!, AlbumIdentity.FromSong(song!), ct);
        track.State = "tagged"; track.UpdatedAtUtc = DateTime.UtcNow; await Save(job, ct);
        await Checkpoint("tagged", track, ct);
        await Verify(track, song!, track.StagingPath, ct);
        track.CandidateSha256 = Hash(track.StagingPath);
        track.State = "verified"; track.UpdatedAtUtc = DateTime.UtcNow; await Save(job, ct);
        await Checkpoint("verified", track, ct);
        // The gate excludes legacy writers and other jobs through backup, rename and final receipt.
        await guard.CheckAsync(ct);
        await db.Entry(song!).ReloadAsync(ct);
        await ManualReviewMetadata.LoadAsync(db, [song!], ct);
        if (!ReviewCopyJobs.Matches(song!, track.AfterJson)) Fail("metadata", "plan_superseded");
        if (Hash(track.SourcePath) != track.SourceSha256 || Hash(track.StagingPath) != track.CandidateSha256)
            Fail("publication", "content_changed_after_verification");
        await CheckDestination(job, track, ct);
        var currentHash = Hash(track.DestinationPath);
        if (currentHash != track.DestinationBeforeSha256)
        {
            // A crash in a case-only rename may leave the old name in the retained directory.
            if (currentHash is not null || Hash(track.BackupPath) != track.DestinationBeforeSha256)
                Fail("publication", "destination_changed");
        }
        track.State = "publishing"; track.UpdatedAtUtc = DateTime.UtcNow; await Save(job, ct);
        await Checkpoint("publishing", track, ct);
        if (track.DestinationBeforeSha256 is not null)
        {
            if (!files.File.Exists(track.BackupPath)) files.File.Copy(track.DestinationPath, track.BackupPath, overwrite: false);
            if (Hash(track.BackupPath) != track.DestinationBeforeSha256) Fail("publication", "backup_not_verified");
        }
        await Checkpoint("backedUp", track, ct);
        // Explicit two-step case move. The verified backup survives interruption at either rename.
        if (track.DestinationBeforeSha256 is not null && track.OldDestinationPath is { } old && old != track.DestinationPath
            && string.Equals(old, track.DestinationPath, StringComparison.OrdinalIgnoreCase)
            && files.File.Exists(old) && !files.File.Exists(track.BackupPath + ".case"))
            files.File.Move(old, track.BackupPath + ".case", overwrite: false);
        await Checkpoint("beforeRename", track, ct);
        await guard.CheckAsync(ct);
        var immediatelyBeforeRename = Hash(track.DestinationPath);
        if (immediatelyBeforeRename != track.DestinationBeforeSha256
            && !(immediatelyBeforeRename is null && track.DestinationBeforeSha256 is not null
                && Hash(track.BackupPath) == track.DestinationBeforeSha256))
            Fail("publication", "destination_changed");
        files.File.Move(track.StagingPath, track.DestinationPath, overwrite: track.DestinationBeforeSha256 is not null);
        await Checkpoint("renamed", track, ct);
        await Verify(track, song!, track.DestinationPath, ct);
        if (Hash(track.DestinationPath) != track.CandidateSha256) Fail("publication", "published_content_changed");
        await Finish(job, track, song!, ct);
    }

    private async Task CheckDestination(ReviewCopyJob job, ReviewCopyTrack track, CancellationToken ct)
    {
        var path = track.DestinationPath.ToLowerInvariant();
        var occupied = await db.Songs.IgnoreQueryFilters().AnyAsync(s => s.Id != track.SongId && s.DeletedAtUtc == null
            && ((s.DestinationPath != null && s.DestinationPath.ToLower() == path)
                || (s.PreviousDestinationPath != null && s.PreviousDestinationPath.ToLower() == path)), ct);
        var held = await db.ReviewCopyTracks.AnyAsync(t => t.JobId != job.Id && t.SongId != track.SongId
            && (t.DestinationPath.ToLower() == path || t.BackupPath.ToLower() == path
                || (t.OldDestinationPath != null && t.OldDestinationPath.ToLower() == path)
                || (t.OldPreviousPath != null && t.OldPreviousPath.ToLower() == path)), ct);
        if (occupied || held) Fail("publication", "destination_held_by_another_track");
    }

    private async Task Verify(ReviewCopyTrack track, SongMetadata song, string path, CancellationToken ct)
    {
        var tags = ReviewTagVerifier.Verify(path, song);
        if (tags.Count > 0) Fail("tags", "tag_mismatch:" + string.Join(',', tags));
        var result = await audio.VerifyAsync(track.SourcePath, path, ct);
        if (!result.Verified) Fail("audio:" + result.FailedStage, result.FailureCode ?? "audio_not_verified");
        if (result.Source!.ContentSha256 != track.SourceSha256) Fail("source", "source_changed_after_verification");
        track.ProofJson = JsonSerializer.Serialize(new { tagsVersion = ReviewTagVerifier.Version, audio = result }, ReviewCopyJobs.Json);
    }
    private async Task Finish(ReviewCopyJob job, ReviewCopyTrack track, SongMetadata song, CancellationToken ct)
    {
        song.MarkBuildDone(track.DestinationPath);
        // No deletion/prune. Every old path also stays in the durable manifest.
        song.PreviousDestinationPath = track.OldDestinationPath == track.DestinationPath
            || string.Equals(track.OldDestinationPath, track.DestinationPath, StringComparison.OrdinalIgnoreCase)
            ? track.DestinationBeforeSha256 is null ? track.OldPreviousPath : track.BackupPath : track.OldDestinationPath ?? track.OldPreviousPath;
        track.State = "published"; track.PublishedAtUtc ??= DateTime.UtcNow;
        track.UpdatedAtUtc = DateTime.UtcNow; track.FailureCode = null; track.FailureStage = null;
        await Save(job, ct);
        await Checkpoint("acknowledged", track, ct);
    }
    private async Task Save(ReviewCopyJob job, CancellationToken ct)
    { job.UpdatedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
    private Task Checkpoint(string stage, ReviewCopyTrack track, CancellationToken ct) => checkpoints?.ReachedAsync(stage, track, ct) ?? Task.CompletedTask;
    private string? Hash(string path) => ReviewCopyJobs.HashIfPresent(files, path);
    private static void Fail(string stage, string code) => throw new ReviewCopyFailure(stage, code);
}
