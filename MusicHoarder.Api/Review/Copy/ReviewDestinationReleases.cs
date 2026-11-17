using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review.Copy;

/// <summary>Explicit release of a destination that only finished review copies still reserve.
/// Strict preconditions: no file at the path, no active song building there, no unfinished job.
/// A released job keeps its history but can no longer be rolled back onto that path.</summary>
internal static class ReviewDestinationReleases
{
    internal static async Task<ReviewDestinationReleaseResult> ReleaseAsync(MusicHoarderDbContext db, IFileSystem files,
        Guid owner, ReviewDestinationReleaseRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty) throw Error(400, "invalid_request_key", "Use a nonempty request UUID.");
        if (string.IsNullOrWhiteSpace(request.DestinationPath) || !files.Path.IsPathRooted(request.DestinationPath))
            throw Error(400, "invalid_destination", "Give the exact absolute destination path.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw Error(400, "reason_required", "Explain why the reservation can be released.");
        var path = request.DestinationPath;
        var folded = path.ToLowerInvariant();
        var existing = await db.ReviewDestinationReleases.IgnoreQueryFilters()
            .SingleOrDefaultAsync(r => r.OwnerUserId == owner && r.RequestId == request.RequestId, ct);
        if (existing is not null)
            return existing.DestinationPath == path ? await ResultAsync(db, existing, ct)
                : throw Error(409, "request_key_reused", "This request UUID already released another path.");

        await using var guard = await ReviewWriteGuard.AcquireAsync(db, false, ct)
            ?? throw Error(409, "writer_busy", "A library writer is active. Retry later; nothing was released.");
        if (files.File.Exists(path)) throw Error(409, "destination_file_present", "A file still exists at this path; nothing was released.");
        var recorders = await db.Songs.IgnoreQueryFilters().Where(s => s.DeletedAtUtc == null
            && s.DestinationPath != null && s.DestinationPath.ToLower() == folded
            && (request.ForSongId == null || s.Id != request.ForSongId || s.OwnerUserId != owner)).ToListAsync(ct);
        // A duplicate the owner resolved after its review settled no longer owns this place, and its file is
        // verified gone (set aside, never deleted): its pointer is stale and is cleared below. Anything else,
        // a held row or a row whose review still owns it included, keeps the path in use.
        var unsettled = await ReviewPlanSettlement.UnsettledAsync(db, recorders, ct);
        var staleDuplicates = recorders.Where(s => s.OwnerUserId == owner && s.IsDuplicate && !s.AutomationHeld
            && s.ReviewPlanId != null && !unsettled.ContainsKey(s.Id)).ToList();
        if (recorders.Count != staleDuplicates.Count)
            throw Error(409, "destination_in_use", "An active song is built at this path; nothing was released.");
        var tracks = await db.ReviewCopyTracks.Where(t => t.DestinationPath.ToLower() == folded || t.BackupPath.ToLower() == folded
            || (t.OldDestinationPath != null && t.OldDestinationPath.ToLower() == folded)
            || (t.OldPreviousPath != null && t.OldPreviousPath.ToLower() == folded)).ToListAsync(ct);
        var jobIds = tracks.Select(t => t.JobId).Distinct().ToArray();
        // Only past generations are released: retained/old paths, or a destination the job once published.
        // A job that merely targets the path keeps its claim (and its rollback).
        var historical = tracks.Where(t => !t.DestinationPath.Equals(path, StringComparison.OrdinalIgnoreCase) || t.State == "published")
            .Select(t => t.JobId).Distinct().Order().ToList();
        var jobs = await db.ReviewCopyJobs.IgnoreQueryFilters().Where(j => jobIds.Contains(j.Id)).ToListAsync(ct);
        if (jobs.Any(j => j.OwnerUserId != owner)) throw Error(409, "foreign_reservation", "Another owner's job reserves this path.");
        if (jobs.Any(j => ReviewPlanSettlement.UnfinishedJobStates.Contains(j.State))) throw Error(409, "job_not_finished", "A review copy using this path is not finished.");
        var previous = await db.Songs.IgnoreQueryFilters().Where(s => s.DeletedAtUtc == null
            && s.PreviousDestinationPath != null && s.PreviousDestinationPath.ToLower() == folded).ToListAsync(ct);
        if (previous.Any(s => s.OwnerUserId != owner)) throw Error(409, "foreign_reservation", "Another owner's song reserves this path.");
        if (historical.Count == 0 && previous.Count == 0 && staleDuplicates.Count == 0)
            throw Error(404, "reservation_not_found", "No historical reservation holds this path.");

        var release = new ReviewDestinationRelease
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, RequestId = request.RequestId, DestinationPath = path, FoldedPath = folded,
            Reason = request.Reason.Trim(), ActorUserId = owner, CreatedAtUtc = DateTime.UtcNow,
            ReleasedJobIds = historical, ClearedPreviousSongIds = previous.Select(s => s.Id).Order().ToList(),
        };
        foreach (var song in previous) song.PreviousDestinationPath = null;
        var evidence = Evidence(release.Id);
        var now = release.CreatedAtUtc;
        foreach (var song in staleDuplicates)
        {
            db.SongMetadataChanges.Add(Audit(song.Id, nameof(SongMetadata.DestinationPath), song.DestinationPath, null, release, evidence));
            song.DestinationPath = null;
        }
        // The keeper this path is released for gets its failed automatic build back in the queue: the obstacle
        // it failed on is gone. Review-owned, held or duplicate rows are not built automatically.
        if (request.ForSongId is int keeperId && await db.Songs.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.Id == keeperId
                && s.OwnerUserId == owner && s.DeletedAtUtc == null, ct) is { } keeper
            && keeper.LibraryBuildStatus == LibraryBuildStatus.Failed && keeper.ReviewPlanId == null
            && !keeper.AutomationHeld && !keeper.IsDuplicate)
        {
            db.SongMetadataChanges.Add(Audit(keeper.Id, nameof(SongMetadata.LibraryBuildStatus), nameof(LibraryBuildStatus.Failed),
                nameof(LibraryBuildStatus.Pending), release, evidence));
            keeper.RequeueFailedBuild();
        }
        db.ReviewDestinationReleases.Add(release);
        await db.SaveChangesAsync(ct);
        return await ResultAsync(db, release, ct);
    }

    internal const string AuditSource = "destination-release";

    private static string Evidence(Guid releaseId) => JsonSerializer.Serialize(new[] { $"{AuditSource}:{releaseId}" });

    // Audited like any other row change, but not revertible from the per-change revert endpoint.
    private static SongMetadataChange Audit(int songId, string field, string? oldValue, string? newValue,
        ReviewDestinationRelease release, string evidence) => new()
    {
        SongId = songId, FieldName = field, OldValue = oldValue, NewValue = newValue, ActorUserId = release.ActorUserId,
        Reason = release.Reason, EvidenceReferencesJson = evidence, Source = AuditSource, Confidence = 1,
        CreatedAtUtc = release.CreatedAtUtc, AppliedAtUtc = release.CreatedAtUtc,
    };

    /// <summary>Jobs whose reservation of <paramref name="foldedPath"/> was explicitly released.</summary>
    internal static async Task<HashSet<Guid>> ReleasedJobIdsAsync(MusicHoarderDbContext db, string foldedPath, CancellationToken ct)
        => (await db.ReviewDestinationReleases.IgnoreQueryFilters().Where(r => r.FoldedPath == foldedPath)
            .Select(r => r.ReleasedJobIds).ToListAsync(ct)).SelectMany(ids => ids).ToHashSet();

    /// <summary>True when any path this job's track touched was released for that job.</summary>
    internal static async Task<bool> TouchesReleasedPathAsync(MusicHoarderDbContext db, ReviewCopyJob job, CancellationToken ct)
    {
        var releases = await db.ReviewDestinationReleases.IgnoreQueryFilters().Where(r => r.OwnerUserId == job.OwnerUserId)
            .Select(r => r.ReleasedJobIds).ToListAsync(ct);
        return releases.Any(ids => ids.Contains(job.Id));
    }

    internal static async Task<IReadOnlyList<ReviewDestinationReleaseResult>> ListAsync(MusicHoarderDbContext db, Guid owner, CancellationToken ct)
    {
        var releases = await db.ReviewDestinationReleases.IgnoreQueryFilters().Where(r => r.OwnerUserId == owner)
            .OrderByDescending(r => r.CreatedAtUtc).Take(500).ToListAsync(ct);
        var audits = await AuditsAsync(db, releases.Select(r => Evidence(r.Id)).ToArray(), ct);
        return releases.Select(r => Result(r, audits)).ToArray();
    }

    private static async Task<ReviewDestinationReleaseResult> ResultAsync(MusicHoarderDbContext db, ReviewDestinationRelease r, CancellationToken ct)
        => Result(r, await AuditsAsync(db, [Evidence(r.Id)], ct));

    private static async Task<List<SongMetadataChange>> AuditsAsync(MusicHoarderDbContext db, string[] evidence, CancellationToken ct)
        => evidence.Length == 0 ? [] : await db.SongMetadataChanges.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Source == AuditSource && c.EvidenceReferencesJson != null && evidence.Contains(c.EvidenceReferencesJson))
            .ToListAsync(ct);

    private static ReviewDestinationReleaseResult Result(ReviewDestinationRelease r, List<SongMetadataChange> audits)
    {
        var own = audits.Where(a => a.EvidenceReferencesJson == Evidence(r.Id)).ToList();
        return new(r.Id, r.RequestId, r.DestinationPath, r.Reason, ReviewTimestamp.Persisted(r.CreatedAtUtc), r.ReleasedJobIds,
            r.ClearedPreviousSongIds,
            own.Where(a => a.FieldName == nameof(SongMetadata.DestinationPath)).Select(a => a.SongId).Order().ToArray(),
            own.Where(a => a.FieldName == nameof(SongMetadata.LibraryBuildStatus)).Select(a => (int?)a.SongId).SingleOrDefault());
    }

    private static ReviewPlanException Error(int status, string code, string message) => new(status, new(code, message));
}
