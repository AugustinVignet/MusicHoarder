using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Endpoints;

public record DuplicateResolveRequest(int KeeperId, int[] LoserIds);

public record DuplicateDismissRequest(int[] SongIds);

/// <summary>A track a resolve could not change, with a stable reason code (no partial writes).</summary>
public record DuplicateResolveBlock(int SongId, string Reason);

/// <summary>
/// Resolution actions for song duplicate clusters. Non-destructive by design: resolving never
/// touches files or soft-deletes rows — losers are flagged <c>IsDuplicate</c> (excluded from
/// build/heal/grading) and remain inspectable and reversible; an already-built loser's destination
/// file is left in place.
/// </summary>
public static class DuplicatesEndpoints
{
    // One detection pass at a time; it's a DB-only sweep, so no JobManager step is involved.
    private static readonly SemaphoreSlim DetectGate = new(1, 1);

    public static IEndpointRouteBuilder MapDuplicatesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/library/duplicates/detect", DetectNow)
            .WithName("DetectDuplicates")
            .WithSummary("Run duplicate detection now (also runs automatically after every fingerprint pass).")
            .WithTags("Library").RequireAdmin();

        app.MapPost("/api/library/duplicates/resolve", Resolve)
            .WithName("ResolveDuplicates")
            .WithSummary("Choose the keeper of a duplicate cluster; the choice is pinned so detection re-runs never overturn it.")
            .WithTags("Library").RequireAdmin();

        app.MapPost("/api/library/duplicates/dismiss", Dismiss)
            .WithName("DismissDuplicates")
            .WithSummary("Mark a set of songs as NOT duplicates of each other; the dismissal persists across detection re-runs.")
            .WithTags("Library").RequireAdmin();

        return app;
    }

    internal static async Task<IResult> DetectNow(IDuplicateDetectionService detection, CancellationToken ct)
    {
        if (!await DetectGate.WaitAsync(0, ct))
            return Results.Conflict(new { message = "Duplicate detection is already running." });

        try
        {
            var result = await detection.DetectDuplicatesAsync(ct);
            return Results.Ok(result);
        }
        finally
        {
            DetectGate.Release();
        }
    }

    internal static async Task<IResult> Resolve(
        DuplicateResolveRequest request, MusicHoarderDbContext db, CancellationToken ct)
    {
        if (request.LoserIds is not { Length: > 0 })
            return Results.BadRequest(new { message = "At least one loser id is required." });
        if (request.LoserIds.Contains(request.KeeperId))
            return Results.BadRequest(new { message = "The keeper cannot also be a loser." });

        var ids = request.LoserIds.Append(request.KeeperId).Distinct().ToList();
        // The per-user query filter scopes the lookup — a foreign song id resolves to "not found".
        var songs = await db.Songs
            .Where(s => ids.Contains(s.Id) && s.DeletedAtUtc == null)
            .ToDictionaryAsync(s => s.Id, ct);

        var missing = ids.Where(id => !songs.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            return Results.NotFound(new { message = $"Song(s) not found: {string.Join(", ", missing)}." });

        var loserIds = request.LoserIds.Distinct().ToArray();
        // A loser still owned by an exact review plan may only change membership once that plan's copy
        // job has stopped. Hold the publication gate so no copy job, retry or rollback moves meanwhile.
        var needsGate = loserIds.Any(id => songs[id].ReviewPlanId.HasValue);
        await using var guard = needsGate ? await ReviewWriteGuard.AcquireAsync(db, false, ct) : null;
        if (needsGate)
        {
            if (guard is null)
                return Results.Conflict(new { code = "writer_busy", message = "A library writer is active. Retry later; nothing was marked." });
            // Re-read under the gate: an Apply or rollback may have moved these rows since the first read.
            foreach (var song in songs.Values) await db.Entry(song).ReloadAsync(ct);
            if (songs.Values.Any(s => s.DeletedAtUtc != null))
                return Results.Conflict(new { code = "selection_changed", message = "A selected track was removed meanwhile; nothing was marked." });
        }

        var keeper = songs[request.KeeperId];
        var blocked = new List<DuplicateResolveBlock>();
        if (keeper.IsDuplicate && keeper.AutomationHeld)
            blocked.Add(new(keeper.Id, "keeper_automation_held"));
        var unsettled = await ReviewPlanSettlement.UnsettledAsync(db, loserIds.Select(id => songs[id]), ct);
        foreach (var loserId in loserIds)
        {
            var loser = songs[loserId];
            if (loser.IsDuplicate && loser.DuplicateOfId == keeper.Id) continue; // already this decision
            if (loser.AutomationHeld) blocked.Add(new(loserId, "automation_held"));
            else if (unsettled.TryGetValue(loserId, out var reason)) blocked.Add(new(loserId, reason));
        }
        // All or nothing: a partial resolve used to answer 200 while silently skipping guarded rows.
        if (blocked.Count > 0)
            return Results.Conflict(new
            {
                code = "duplicates_not_marked",
                message = "Nothing was changed: some tracks cannot take this duplicate decision yet.",
                KeeperId = keeper.Id,
                Blocked = blocked,
            });

        keeper.DuplicateKeeperPinnedAtUtc = DateTime.UtcNow;
        keeper.ClearDuplicate();

        foreach (var loserId in loserIds)
        {
            var loser = songs[loserId];
            if (loser.ReviewPlanId.HasValue) loser.MarkAsDuplicateAfterSettledReview(keeper.Id);
            else loser.MarkAsDuplicate(keeper.Id);
        }

        if (guard is not null) await guard.CheckAsync(ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            KeeperId = keeper.Id,
            LoserIds = loserIds,
            MarkedIds = loserIds.Where(id => songs[id].IsDuplicate && songs[id].DuplicateOfId == keeper.Id).ToArray(),
        });
    }

    internal static async Task<IResult> Dismiss(
        DuplicateDismissRequest request, MusicHoarderDbContext db, ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var ids = request.SongIds?.Distinct().ToList() ?? [];
        if (ids.Count < 2)
            return Results.BadRequest(new { message = "At least two song ids are required." });

        var songs = await db.Songs
            .Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var missing = ids.Where(id => !songs.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            return Results.NotFound(new { message = $"Song(s) not found: {string.Join(", ", missing)}." });

        var idSet = ids.ToHashSet();
        var existingLinks = await db.SongDuplicateLinks
            .Where(l => idSet.Contains(l.SongIdLow) && idSet.Contains(l.SongIdHigh))
            .ToDictionaryAsync(l => (l.SongIdLow, l.SongIdHigh), ct);

        var now = DateTime.UtcNow;
        var ownerId = currentUser.UserId != Guid.Empty ? currentUser.UserId : songs.Values.First().OwnerUserId;

        // Dismiss every pair among the set — creating links detection never made, so future runs
        // pre-respect the decision even if new evidence (e.g. a fingerprint) appears later.
        var ordered = ids.OrderBy(id => id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var pair = (Low: ordered[i], High: ordered[j]);
                if (existingLinks.TryGetValue(pair, out var link))
                {
                    if (link.Status != DuplicateLinkStatus.Dismissed)
                    {
                        link.Status = DuplicateLinkStatus.Dismissed;
                        link.DismissedAtUtc = now;
                    }
                }
                else
                {
                    db.SongDuplicateLinks.Add(new SongDuplicateLink
                    {
                        OwnerUserId = ownerId,
                        SongIdLow = pair.Low,
                        SongIdHigh = pair.High,
                        Status = DuplicateLinkStatus.Dismissed,
                        Confidence = DuplicateConfidence.Suspected,
                        Reasons = DuplicateMatchReason.None,
                        DetectedAtUtc = now,
                        DismissedAtUtc = now,
                    });
                }
            }
        }

        // Un-flag members currently marked duplicates *of each other* (a member flagged against a
        // song outside the dismissed set keeps its flag).
        foreach (var song in songs.Values)
        {
            if (song.IsDuplicate && song.DuplicateOfId is int of && idSet.Contains(of))
                song.ClearDuplicate();
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new { DismissedSongIds = ordered });
    }
}
