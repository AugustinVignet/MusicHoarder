using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

// Resolving a loser bound to a review plan takes the publication gate, whose in-memory fallback is
// process-wide: never run alongside the in-memory builder tests that hold it.
[Collection(ReviewPublicationGateCollection.Name)]
public class DuplicatesEndpointsTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task Resolve_PinsKeeper_FlagsLosers()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac"),
            CreateSong(2, "/b/track.mp3"),
            CreateSong(3, "/c/track.mp3"));
        await db.SaveChangesAsync();

        var result = await DuplicatesEndpoints.Resolve(
            new DuplicateResolveRequest(KeeperId: 2, LoserIds: [1, 3]), db, CancellationToken.None);

        AssertStatus(result, 200);
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
        Assert.NotNull(songs[1].DuplicateKeeperPinnedAtUtc);
        Assert.True(songs[2].IsDuplicate);
        Assert.Equal(2, songs[2].DuplicateOfId);
    }

    [Fact]
    public async Task Resolve_KeeperInLosers_IsBadRequest()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        db.Songs.AddRange(CreateSong(1, "/a/track.flac"), CreateSong(2, "/b/track.mp3"));
        await db.SaveChangesAsync();

        var result = await DuplicatesEndpoints.Resolve(
            new DuplicateResolveRequest(KeeperId: 1, LoserIds: [1, 2]), db, CancellationToken.None);

        AssertStatus(result, 400);
    }

    [Fact]
    public async Task Resolve_ForeignSong_IsNotFound()
    {
        // The per-user query filter must hide another owner's song — resolving against it 404s.
        var options = CreateOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(CreateSong(1, "/a/track.flac", owner: Guid.NewGuid()));
            seed.Songs.Add(CreateSong(2, "/b/track.mp3", owner: Owner));
            await seed.SaveChangesAsync();
        }

        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        var result = await DuplicatesEndpoints.Resolve(
            new DuplicateResolveRequest(KeeperId: 2, LoserIds: [1]), db, CancellationToken.None);

        AssertStatus(result, 404);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("needsAttention")]
    [InlineData("rolledBack")]
    public async Task Resolve_LoserWithSettledReviewPlan_IsMarked_KeepingPlanAndApproval(string jobState)
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        var loser = CreateSong(1, "/a/track.mp3");
        var planId = AddReviewedPlan(db, loser, applied: true, jobState);
        db.Songs.AddRange(loser, CreateSong(2, "/b/track.flac"));
        await db.SaveChangesAsync();

        var result = await DuplicatesEndpoints.Resolve(new DuplicateResolveRequest(2, [1]), db, CancellationToken.None);

        AssertStatus(result, 200);
        var marked = await db.Songs.SingleAsync(s => s.Id == 1);
        Assert.True(marked.IsDuplicate);
        Assert.Equal(2, marked.DuplicateOfId);
        Assert.Equal(planId, marked.ReviewPlanId);
        Assert.True(marked.IsManuallyApproved);
        Assert.NotNull(marked.ManuallyApprovedAtUtc);
    }

    [Theory]
    [InlineData(false, "completed", "review_plan_not_applied")]
    [InlineData(true, "pending", "review_copy_job_unfinished")]
    [InlineData(true, "running", "review_copy_job_unfinished")]
    [InlineData(true, "rollingBack", "review_copy_job_unfinished")]
    [InlineData(true, null, "review_copy_job_missing")]
    public async Task Resolve_LoserWhosePlanStillOwnsIt_IsRefusedAndNothingChanges(bool applied, string? jobState, string reason)
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        var owned = CreateSong(1, "/a/track.mp3");
        AddReviewedPlan(db, owned, applied, jobState);
        var plain = CreateSong(3, "/c/track.mp3");
        db.Songs.AddRange(owned, CreateSong(2, "/b/track.flac"), plain);
        await db.SaveChangesAsync();

        var result = await DuplicatesEndpoints.Resolve(new DuplicateResolveRequest(2, [1, 3]), db, CancellationToken.None);

        AssertStatus(result, 409);
        Assert.Equal(reason, Assert.Single(BlockedReasons(result)).Reason);
        Assert.Equal(1, Assert.Single(BlockedReasons(result)).SongId);
        // All or nothing: the unguarded loser and the keeper pin are not written either.
        var songs = await db.Songs.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        Assert.All(songs, s => Assert.False(s.IsDuplicate));
        Assert.Null(songs[1].DuplicateKeeperPinnedAtUtc);
    }

    [Fact]
    public async Task Resolve_AutomationHeldLoser_IsRefused_HoldIsNotLifted()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        var held = CreateSong(1, "/a/track.mp3");
        AddReviewedPlan(db, held, applied: true, "completed");
        held.AutomationHeld = true;
        db.Songs.AddRange(held, CreateSong(2, "/b/track.flac"));
        await db.SaveChangesAsync();

        var result = await DuplicatesEndpoints.Resolve(new DuplicateResolveRequest(2, [1]), db, CancellationToken.None);

        AssertStatus(result, 409);
        Assert.Equal(new DuplicateResolveBlock(1, "automation_held"), Assert.Single(BlockedReasons(result)));
        var stored = await db.Songs.AsNoTracking().SingleAsync(s => s.Id == 1);
        Assert.False(stored.IsDuplicate);
        Assert.True(stored.AutomationHeld);
    }

    [Fact]
    public async Task Resolve_ReplayOfAnAppliedDecision_IsIdempotent()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        var loser = CreateSong(1, "/a/track.mp3");
        AddReviewedPlan(db, loser, applied: true, "completed");
        db.Songs.AddRange(loser, CreateSong(2, "/b/track.flac"));
        await db.SaveChangesAsync();

        AssertStatus(await DuplicatesEndpoints.Resolve(new DuplicateResolveRequest(2, [1]), db, CancellationToken.None), 200);
        // A later hold does not turn an identical replay into a refusal.
        (await db.Songs.SingleAsync(s => s.Id == 1)).AutomationHeld = true;
        await db.SaveChangesAsync();
        AssertStatus(await DuplicatesEndpoints.Resolve(new DuplicateResolveRequest(2, [1]), db, CancellationToken.None), 200);
        Assert.Equal(2, (await db.Songs.AsNoTracking().SingleAsync(s => s.Id == 1)).DuplicateOfId);
    }

    [Fact]
    public async Task Dismiss_CreatesDismissedLinks_AndClearsInSetFlags()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        var a = CreateSong(1, "/a/track.flac");
        var b = CreateSong(2, "/b/track.mp3");
        b.MarkAsDuplicate(1);
        var c = CreateSong(3, "/c/other.mp3");
        c.MarkAsDuplicate(99); // flagged against a song OUTSIDE the dismissed set — must keep its flag
        db.Songs.AddRange(a, b, c);
        db.SongDuplicateLinks.Add(new SongDuplicateLink
        {
            OwnerUserId = Owner,
            SongIdLow = 1,
            SongIdHigh = 2,
            Status = DuplicateLinkStatus.Active,
            Confidence = DuplicateConfidence.Confirmed,
            Reasons = DuplicateMatchReason.ExactFingerprint,
            DetectedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await DuplicatesEndpoints.Dismiss(
            new DuplicateDismissRequest([1, 2, 3]), db, new StubCurrentUser(Owner), CancellationToken.None);

        AssertStatus(result, 200);

        // All 3 pairs among the set are dismissed: the existing link flipped, the other two created.
        var links = await db.SongDuplicateLinks.OrderBy(l => l.SongIdLow).ThenBy(l => l.SongIdHigh).ToListAsync();
        Assert.Equal(3, links.Count);
        Assert.All(links, l => Assert.Equal(DuplicateLinkStatus.Dismissed, l.Status));
        Assert.All(links, l => Assert.NotNull(l.DismissedAtUtc));

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.False(songs[1].IsDuplicate); // was flagged against song 1 (in set) — cleared
        Assert.True(songs[2].IsDuplicate);  // flagged against 99 (outside set) — kept
    }

    [Fact]
    public async Task Dismiss_FewerThanTwoIds_IsBadRequest()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));

        var result = await DuplicatesEndpoints.Dismiss(
            new DuplicateDismissRequest([1]), db, new StubCurrentUser(Owner), CancellationToken.None);

        AssertStatus(result, 400);
    }

    private static Guid AddReviewedPlan(MusicHoarderDbContext db, SongMetadata song, bool applied, string? jobState)
    {
        var plan = new ReviewPlan
        {
            Id = Guid.NewGuid(), OwnerUserId = Owner, ActorUserId = Owner, CreatedAtUtc = DateTime.UtcNow,
            Revision = "r1", AppliedAtUtc = applied ? DateTime.UtcNow : null,
        };
        db.ReviewPlans.Add(plan);
        if (jobState is not null)
            db.ReviewCopyJobs.Add(new ReviewCopyJob
            {
                Id = Guid.NewGuid(), OwnerUserId = Owner, PlanId = plan.Id, RequestId = Guid.NewGuid(), Revision = "r1",
                State = jobState, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
        song.LockManualApproval();
        song.ReviewPlanId = plan.Id;
        return plan.Id;
    }

    private static IReadOnlyList<DuplicateResolveBlock> BlockedReasons(Microsoft.AspNetCore.Http.IResult result)
    {
        var value = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IValueHttpResult>(result).Value!;
        Assert.Equal("duplicates_not_marked", value.GetType().GetProperty("code")?.GetValue(value));
        return Assert.IsAssignableFrom<IReadOnlyList<DuplicateResolveBlock>>(value.GetType().GetProperty("Blocked")!.GetValue(value));
    }

    private static void AssertStatus(Microsoft.AspNetCore.Http.IResult result, int expected)
    {
        var status = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(expected, status.StatusCode);
    }

    private static DbContextOptions CreateOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private static SongMetadata CreateSong(int id, string sourcePath, Guid? owner = null) => new()
    {
        OwnerUserId = owner ?? Owner,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1_000_000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Test Artist",
        Title = "Test Track",
    };

    private sealed class StubCurrentUser(Guid userId) : ICurrentUserAccessor
    {
        public CurrentUser? User { get; } = new(userId, "owner@test", UserRole.Admin, "Owner");
        public Guid UserId => userId;
    }
}

[CollectionDefinition(Name)]
public sealed class ReviewPublicationGateCollection
{
    public const string Name = "Review publication gate (in-memory)";
}
