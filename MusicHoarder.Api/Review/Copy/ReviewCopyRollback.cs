using System.Data;
using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review.Copy;

/// <summary>Explicit rollback with durable intent. Keeps all audits and both generations of files.
/// It refuses a later metadata correction or a modified published file before touching anything.</summary>
internal static class ReviewCopyRollback
{
    internal static async Task RunAsync(MusicHoarderDbContext db, IFileSystem files, Guid owner, Guid id, CancellationToken ct, ReviewWriteGuard? heldGuard = null)
    {
        await using var acquired = heldGuard is null ? await ReviewWriteGuard.AcquireAsync(db, false, ct) : null;
        var guard = heldGuard ?? acquired ?? throw new ReviewPlanException(409, new("writer_busy", "A library writer is active. Retry later."));
        db.ChangeTracker.Clear();
        var job = await ReviewCopyJobs.OwnedAsync(db, owner, id, ct);
        if (job.State == "rolledBack") return;
        var ids = job.Tracks.Select(t => t.SongId).ToArray();
        var songs = await db.Songs.IgnoreQueryFilters().Where(s => s.OwnerUserId == owner && ids.Contains(s.Id)).ToListAsync(ct);
        await ManualReviewMetadata.LoadAsync(db, songs, ct);
        foreach (var track in job.Tracks)
        {
            var song = songs.SingleOrDefault(s => s.Id == track.SongId);
            if (track.BeforeJson.Length == 0) Refuse("rollback_baseline_unavailable");
            if (song is null || !ReviewCopyJobs.Matches(song!, track.AfterJson)) Refuse("later_correction");
            var currentHash = ReviewCopyJobs.HashIfPresent(files, track.DestinationPath);
            var backupHash = ReviewCopyJobs.HashIfPresent(files, track.BackupPath);
            var publicationStarted = track.PublishedAtUtc is not null || backupHash is not null;
            if (publicationStarted && currentHash != track.CandidateSha256)
            {
                var originalStillPresent = currentHash == track.DestinationBeforeSha256;
                var caseMoveGap = currentHash is null && backupHash == track.DestinationBeforeSha256;
                if (!originalStillPresent && !caseMoveGap) Refuse("published_content_changed");
                if (track.PublishedAtUtc is not null && job.State != "rollingBack") Refuse("published_content_changed");
            }
            if (track.DestinationBeforeSha256 is not null && publicationStarted
                && track.DestinationBeforeSha256 != backupHash) Refuse("backup_not_verified");
        }
        job.State = "rollingBack"; job.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        foreach (var track in job.Tracks)
        {
            if (track.DestinationBeforeSha256 is null || !files.File.Exists(track.BackupPath)) continue;
            var retained = track.BackupPath + ".replacement";
            if (!files.File.Exists(retained))
            {
                var candidate = ReviewCopyJobs.HashIfPresent(files, track.DestinationPath) == track.CandidateSha256
                    ? track.DestinationPath : track.StagingPath;
                if (ReviewCopyJobs.HashIfPresent(files, candidate) != track.CandidateSha256) Refuse("replacement_not_retained");
                files.File.Copy(candidate, retained, overwrite: false);
            }
            if (ReviewCopyJobs.HashIfPresent(files, retained) != track.CandidateSha256) Refuse("replacement_not_retained");
            var restore = track.BackupPath + ".restore";
            files.File.Copy(track.BackupPath, restore, overwrite: true);
            if (ReviewCopyJobs.HashIfPresent(files, restore) != track.DestinationBeforeSha256) Refuse("restore_not_verified");
            await guard.CheckAsync(ct);
            var restorePath = track.OldDestinationPath is { } old && string.Equals(old, track.DestinationPath, StringComparison.OrdinalIgnoreCase)
                ? old : track.DestinationPath;
            if (restorePath != track.DestinationPath && files.File.Exists(track.DestinationPath))
            {
                // The new generation has a verified retained copy; move the case alias away so
                // the restored name is spelled exactly as the old metadata records it.
                var displaced = track.BackupPath + ".rollback-case";
                if (!files.File.Exists(displaced)) files.File.Move(track.DestinationPath, displaced, overwrite: false);
            }
            files.File.Move(restore, restorePath, overwrite: true);
        }
        // Metadata rollback is one retryable transaction, after recoverable filesystem restoration.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var currentJob = await ReviewCopyJobs.OwnedAsync(db, owner, id, token);
            if (currentJob.State == "rolledBack") return;
            var currentSongs = await db.Songs.IgnoreQueryFilters().Where(s => s.OwnerUserId == owner && ids.Contains(s.Id)).ToListAsync(token);
            await ManualReviewMetadata.LoadAsync(db, currentSongs, token);
            foreach (var track in currentJob.Tracks)
            {
                var song = currentSongs.Single(s => s.Id == track.SongId);
                if (!ReviewCopyJobs.Matches(song, track.AfterJson)) Refuse("later_correction");
                var before = ReviewCopyJobs.Read(track.BeforeJson);
                foreach (var (field, value) in before.Fields) ReviewFields.Restore(song, field, value);
                song.ReviewPlanId = before.PlanId;
                song.IsManuallyApproved = before.IsManuallyApproved; song.ManuallyApprovedAtUtc = before.ApprovedAtUtc;
                song.ManualReviewValues = before.ManualValues; song.EnrichmentStatus = before.Status;
                song.IsDuplicate = before.IsDuplicate; song.DuplicateOfId = before.DuplicateOfId;
                song.DestinationPath = track.OldDestinationPath; song.PreviousDestinationPath = track.OldPreviousPath;
                song.LibraryBuildStatus = before.BuildStatus; song.LibraryBuiltAtUtc = before.BuiltAtUtc;
                track.State = "rolledBack"; track.UpdatedAtUtc = DateTime.UtcNow;
            }
            var audits = await db.SongMetadataChanges.IgnoreQueryFilters().Where(a => ids.Contains(a.SongId)
                && a.ReviewPlanRevision == currentJob.Revision && a.RevertedAtUtc == null).ToListAsync(token);
            foreach (var audit in audits) audit.RevertedAtUtc = DateTime.UtcNow;
            currentJob.State = "rolledBack"; currentJob.RolledBackAtUtc = DateTime.UtcNow; currentJob.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        }, ct);
    }
    private static void Refuse(string code) => throw new ReviewPlanException(409, new(code, "Rollback refused: the recorded baseline or current correction cannot be safely restored."));
}
