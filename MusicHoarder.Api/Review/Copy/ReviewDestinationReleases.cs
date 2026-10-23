using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review.Copy;

/// <summary>Explicit release of a destination that only finished review copies still reserve.
/// Strict preconditions: no file at the path, no active song building there, no unfinished job.
/// A released job keeps its history but can no longer be rolled back onto that path.</summary>
internal static class ReviewDestinationReleases
{
    private static readonly string[] UnfinishedStates = ["pending", "running", "rollingBack"];

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
            return existing.DestinationPath == path ? Result(existing)
                : throw Error(409, "request_key_reused", "This request UUID already released another path.");

        await using var guard = await ReviewWriteGuard.AcquireAsync(db, false, ct)
            ?? throw Error(409, "writer_busy", "A library writer is active. Retry later; nothing was released.");
        if (files.File.Exists(path)) throw Error(409, "destination_file_present", "A file still exists at this path; nothing was released.");
        var inUse = await db.Songs.IgnoreQueryFilters().AnyAsync(s => s.DeletedAtUtc == null
            && s.DestinationPath != null && s.DestinationPath.ToLower() == folded, ct);
        if (inUse) throw Error(409, "destination_in_use", "An active song is built at this path; nothing was released.");
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
        if (jobs.Any(j => UnfinishedStates.Contains(j.State))) throw Error(409, "job_not_finished", "A review copy using this path is not finished.");
        var previous = await db.Songs.IgnoreQueryFilters().Where(s => s.DeletedAtUtc == null
            && s.PreviousDestinationPath != null && s.PreviousDestinationPath.ToLower() == folded).ToListAsync(ct);
        if (previous.Any(s => s.OwnerUserId != owner)) throw Error(409, "foreign_reservation", "Another owner's song reserves this path.");
        if (historical.Count == 0 && previous.Count == 0) throw Error(404, "reservation_not_found", "No historical reservation holds this path.");

        var release = new ReviewDestinationRelease
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, RequestId = request.RequestId, DestinationPath = path, FoldedPath = folded,
            Reason = request.Reason.Trim(), ActorUserId = owner, CreatedAtUtc = DateTime.UtcNow,
            ReleasedJobIds = historical, ClearedPreviousSongIds = previous.Select(s => s.Id).Order().ToList(),
        };
        foreach (var song in previous) song.PreviousDestinationPath = null;
        db.ReviewDestinationReleases.Add(release);
        await db.SaveChangesAsync(ct);
        return Result(release);
    }

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
        => (await db.ReviewDestinationReleases.IgnoreQueryFilters().Where(r => r.OwnerUserId == owner)
            .OrderByDescending(r => r.CreatedAtUtc).Take(500).ToListAsync(ct)).Select(Result).ToArray();

    private static ReviewDestinationReleaseResult Result(ReviewDestinationRelease r) => new(r.Id, r.RequestId, r.DestinationPath,
        r.Reason, ReviewTimestamp.Persisted(r.CreatedAtUtc), r.ReleasedJobIds, r.ClearedPreviousSongIds);

    private static ReviewPlanException Error(int status, string code, string message) => new(status, new(code, message));
}
