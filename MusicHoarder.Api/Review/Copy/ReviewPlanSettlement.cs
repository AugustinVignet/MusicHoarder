using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review.Copy;

/// <summary>
/// When a row's exact review plan has finished with it. A plan owns its rows from Apply until its
/// copy job stops: until then no duplicate decision may change their membership, and their built
/// path stays claimed. "Settled" = the plan is applied and every copy job queued for it has stopped
/// (completed, needsAttention or rolledBack). A plan without a job is never settled.
/// </summary>
internal static class ReviewPlanSettlement
{
    internal static readonly string[] UnfinishedJobStates = ["pending", "running", "rollingBack"];

    internal const string PlanNotApplied = "review_plan_not_applied";
    internal const string CopyJobMissing = "review_copy_job_missing";
    internal const string CopyJobUnfinished = "review_copy_job_unfinished";

    /// <summary>Why each row's review plan does not let it be marked a duplicate yet. Rows without a
    /// plan, or whose plan is settled, are absent from the result.</summary>
    internal static async Task<Dictionary<int, string>> UnsettledAsync(MusicHoarderDbContext db,
        IEnumerable<SongMetadata> songs, CancellationToken ct)
    {
        var planned = songs.Where(s => s.ReviewPlanId.HasValue).ToList();
        if (planned.Count == 0) return [];
        var planIds = planned.Select(s => s.ReviewPlanId!.Value).Distinct().ToArray();
        var applied = (await db.ReviewPlans.IgnoreQueryFilters().AsNoTracking()
                .Where(p => planIds.Contains(p.Id) && p.AppliedAtUtc != null)
                .Select(p => new { p.Id, p.OwnerUserId }).ToListAsync(ct))
            .ToDictionary(p => p.Id, p => p.OwnerUserId);
        var jobs = await db.ReviewCopyJobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => planIds.Contains(j.PlanId)).Select(j => new { j.PlanId, j.OwnerUserId, j.State }).ToListAsync(ct);
        var result = new Dictionary<int, string>();
        foreach (var song in planned)
        {
            var plan = song.ReviewPlanId!.Value;
            if (!applied.TryGetValue(plan, out var planOwner) || planOwner != song.OwnerUserId) { result[song.Id] = PlanNotApplied; continue; }
            var planJobs = jobs.Where(j => j.PlanId == plan && j.OwnerUserId == song.OwnerUserId).ToList();
            if (planJobs.Count == 0) result[song.Id] = CopyJobMissing;
            else if (planJobs.Any(j => UnfinishedJobStates.Contains(j.State))) result[song.Id] = CopyJobUnfinished;
        }
        return result;
    }

    /// <summary>
    /// Translatable predicate: the row still claims its built destination path against automatic builds.
    /// A live row does; a duplicate-marked row does only while automation holds it or while its exact
    /// review plan is not settled (a plain duplicate never did). Physical guards — a file present at the
    /// path, a review copy reservation — are separate and unchanged.
    /// </summary>
    internal static Expression<Func<SongMetadata, bool>> ClaimsBuiltDestination(MusicHoarderDbContext db) => Claims(db, false);

    /// <summary>Same rule for an already-built album slot (disc, track), except that a plain duplicate's
    /// built copy keeps representing its slot as before: only an owner's duplicate decision taken after
    /// a settled review frees the slot for its keeper.</summary>
    internal static Expression<Func<SongMetadata, bool>> ClaimsBuiltPosition(MusicHoarderDbContext db) => Claims(db, true);

    private static Expression<Func<SongMetadata, bool>> Claims(MusicHoarderDbContext db, bool plainDuplicatesClaim)
    {
        var unfinished = UnfinishedJobStates;
        return s => !s.IsDuplicate || s.AutomationHeld
            || (s.ReviewPlanId == null && plainDuplicatesClaim)
            || (s.ReviewPlanId != null && !(
                db.ReviewPlans.IgnoreQueryFilters().Any(p => p.Id == s.ReviewPlanId && p.OwnerUserId == s.OwnerUserId && p.AppliedAtUtc != null)
                && db.ReviewCopyJobs.IgnoreQueryFilters().Any(j => j.PlanId == s.ReviewPlanId && j.OwnerUserId == s.OwnerUserId)
                && !db.ReviewCopyJobs.IgnoreQueryFilters().Any(j => j.PlanId == s.ReviewPlanId && unfinished.Contains(j.State))));
    }
}
