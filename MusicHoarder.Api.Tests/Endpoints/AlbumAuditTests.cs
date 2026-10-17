using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicHoarder.Api.Auth;
using System.Diagnostics;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Tests.Auth;
using Store = MusicHoarder.Api.Tests.Endpoints.AlbumReviewContextTests.TestDatabase;

namespace MusicHoarder.Api.Tests.Endpoints;

public sealed class AlbumAuditTests
{
    private static readonly Guid Owner = TestUsers.OwnerId;
    private static readonly TestCurrentUserAccessor User = new(TestCurrentUserAccessor.OwnerUser);
    private static readonly FileSystem Files = new();
    private static readonly IDestinationPathResolver Resolver = new DestinationPathResolver(
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { DestinationDirectory = "/synthetic/destination" }));
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static SongMetadata Song(int id, string album = "Program", string parent = "/synthetic/flat") => new()
    {
        Id = id, OwnerUserId = Owner, SourcePath = $"{parent}/{id}.flac", FileName = $"{id}.flac", Extension = ".flac",
        FileSizeBytes = 100, LastModifiedUtc = Stamp, IndexedAtUtc = Stamp, Title = $"Movement {id}",
        AlbumArtist = "Example Ensemble", Artist = "Example Ensemble", Album = album, TrackNumber = id,
        DiscNumber = 1, EnrichmentStatus = EnrichmentStatus.Matched, DurationMs = 120000
    };
    private static T Value<T>(IResult result) => Assert.IsType<T>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    private static async Task<AlbumAuditContext> Read(MusicHoarderDbContext db, int[] ids, AlbumAuditReference? reference = null,
        string? cursor = null, int limit = 25) => Value<AlbumAuditContext>(await AlbumAuditEndpoints.Context(new(ids, reference, cursor, limit), db, User, Files, default));
    private static ReviewPlanRequest Plan(AlbumAuditContext context, int id = 1) => new(context.PlanContext!.Scope,
        context.ContextRevision, "Synthetic booklet inspection", ["fixture:booklet"],
        [new(id, false, new() { ["Genre"] = JsonSerializer.SerializeToElement("Example genre") })]);

    [PostgresFact]
    public async Task InventoryCoversEveryOwnedActiveTrack_WholeGroups_SearchAndStatus_NoSideEffects()
    {
        await using var store = await Store.CreateAsync();
        var songs = Enumerable.Range(1, 7).Select(i => Song(i)).ToArray();
        songs[0].DestinationPath = "/library/Program/1.flac"; songs[0].LibraryBuildStatus = LibraryBuildStatus.Done;
        songs[1].DestinationPath = "/library/Program/2.flac"; songs[1].EnrichmentStatus = EnrichmentStatus.Pending;
        songs[2].EnrichmentStatus = EnrichmentStatus.NeedsReview; songs[2].IsDuplicate = true;
        songs[3].SourcePath = ""; songs[3].Album = null;
        songs[4].OwnerUserId = TestUsers.DemoId; songs[5].DeletedAtUtc = Stamp;
        store.Db.AddRange(songs); await store.Db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(await store.Db.Songs.AsNoTracking().OrderBy(s => s.Id).ToArrayAsync());
        var page = await AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 100, null, default);
        Assert.Equal(5, page.Coverage.ActiveTracks); Assert.Equal(3, page.Coverage.PrimaryGroups);
        Assert.Equal(new[] { 1, 2, 3, 4, 7 }, page.Groups.SelectMany(g => g.MemberIds).Order());
        var selected = await AlbumAuditReader.InventoryAsync(store.Db, Owner, "Movement 2", "Pending", 100, null, default);
        var group = Assert.Single(selected.Groups); Assert.Equal(new[] { 1, 2 }, group.MemberIds); Assert.Equal(1, group.BuiltCount);
        Assert.Equal(1, group.StatusCounts["Pending"]); Assert.Equal(1, group.StatusCounts["Matched"]);
        Assert.Equal(before, JsonSerializer.Serialize(await store.Db.Songs.AsNoTracking().OrderBy(s => s.Id).ToArrayAsync()));
        Assert.DoesNotContain(store.Db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    [PostgresFact]
    public async Task KeysetPassesTenThousandGroups_WithBoundedMaterialization_AndStableActivityRevision()
    {
        await using var store = await Store.CreateAsync();
        store.Db.Songs.AddRange(Enumerable.Range(1, 10013).Select(i => Song(i, parent: $"/synthetic/folder-{i:00000}")));
        await store.Db.SaveChangesAsync(); store.Db.ChangeTracker.Clear();
        var trace = new SqlTrace();
        await using var observed = new MusicHoarderDbContext(new DbContextOptionsBuilder()
            .UseNpgsql(store.ConnectionString).AddInterceptors(trace).Options);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var firstMeasured = await AlbumAuditReader.InventoryAsync(observed, Owner, null, null, 1, null, default);
        Assert.Equal(3, trace.Statements.Count);
        Assert.All(trace.Statements, sql => { Assert.DoesNotContain("Lyrics", sql); Assert.DoesNotContain("Fingerprint", sql); });
        Console.WriteLine($"E24 first page: SQL={trace.Statements.Count}, allocatedBytes={GC.GetTotalAllocatedBytes(true) - allocatedBefore}, responseBytes={JsonSerializer.SerializeToUtf8Bytes(firstMeasured).Length}");
        var clock = Stopwatch.StartNew();
        var maxPageBytes = 0;
        var first = await AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 100, null, default);
        var seen = first.Groups.Select(g => g.GroupKey).ToHashSet(StringComparer.Ordinal);
        var page = first;
        while (page.NextCursor is { } cursor)
        {
            page = await AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 100, cursor, default);
            Assert.All(page.Groups, g => Assert.True(seen.Add(g.GroupKey)));
            maxPageBytes = Math.Max(maxPageBytes, JsonSerializer.SerializeToUtf8Bytes(page).Length);
            Assert.True(maxPageBytes < 250000);
        }
        Assert.Equal(10013, seen.Count); Assert.Empty(store.Db.ChangeTracker.Entries());
        await store.Db.Songs.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.PlayCount, 10).SetProperty(x => x.LikedAtUtc, Stamp));
        Assert.Equal(first.InventoryRevision, (await AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 1, null, default)).InventoryRevision);
        Console.WriteLine($"E24 SQL keyset 10013 groups: {clock.ElapsedMilliseconds} ms; tracked entities=0; maximum page bytes={maxPageBytes}; no filesystem dependency.");
    }

    [PostgresTheory]
    [InlineData("add")]
    [InlineData("move")]
    [InlineData("delete")]
    public async Task InventoryDriftRejectsContinuation(string kind)
    {
        await using var store = await Store.CreateAsync();
        store.Db.AddRange(Song(1, parent: "/a"), Song(2, parent: "/b")); await store.Db.SaveChangesAsync();
        var page = await AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 1, null, default);
        if (kind == "add") store.Db.Add(Song(3));
        if (kind == "move") store.Db.Songs.Local.Single(s => s.Id == 1).SourcePath = "/changed/1.flac";
        if (kind == "delete") store.Db.Songs.Local.Single(s => s.Id == 1).DeletedAtUtc = Stamp;
        await store.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<AlbumReviewReadException>(() => AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 1, page.NextCursor, default));
        Assert.Equal("inventory_changed", error.Error.Code);
        if (kind == "move")
        {
            var fresh = await AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 100, null, default);
            Assert.DoesNotContain(fresh.Groups, g => g.GroupKey == "v1:f:/a");
            Assert.Equal(1, Assert.Single(fresh.Groups, g => g.GroupKey == "v1:f:/changed").MemberIds.Single());
        }
    }

    [PostgresFact]
    public async Task CursorsAreBoundToOwnerAndFilters_AndValidateBounds()
    {
        await using var store = await Store.CreateAsync(); store.Db.AddRange(Song(1, parent: "/a"), Song(2, parent: "/b")); await store.Db.SaveChangesAsync();
        var page = await AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 1, null, default);
        foreach (var cursor in new[] { "invalid", new string('a', 8193) })
            Assert.Equal(400, (await Assert.ThrowsAsync<AlbumReviewReadException>(() => AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 1, cursor, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<AlbumReviewReadException>(() => AlbumAuditReader.InventoryAsync(store.Db, TestUsers.DemoId, null, null, 1, page.NextCursor, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<AlbumReviewReadException>(() => AlbumAuditReader.InventoryAsync(store.Db, Owner, "changed", null, 1, page.NextCursor, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<AlbumReviewReadException>(() => AlbumAuditReader.InventoryAsync(store.Db, Owner, null, "unknown", 1, null, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<AlbumReviewReadException>(() => AlbumAuditReader.InventoryAsync(store.Db, Owner, new string('a', 257), null, 1, null, default))).StatusCode);
    }

    [PostgresFact]
    public async Task FlatFiveThousandFolder_ExplicitFourteenMembers_CanPreviewApply_WhileLegacyR2StillRefuses()
    {
        await using var store = await Store.CreateAsync();
        store.Db.AddRange(Enumerable.Range(1, 5000).Select(i => Song(i, i <= 14 ? "Program" : $"Unrelated {i}")));
        await store.Db.SaveChangesAsync(); store.Db.ChangeTracker.Clear();
        var context = await Read(store.Db, Enumerable.Range(1, 14).ToArray(), limit: 10);
        Assert.Equal(14, context.Members.Count); Assert.Equal(4986, context.RelatedCount); Assert.Equal(10, context.Related.Count);
        Assert.True(context.PlanReady); Assert.False(context.RelatedComplete); Assert.NotNull(context.NextCursor); Assert.Empty(context.BlockingRelated);
        Assert.False(context.ContentVerified); Assert.Equal("unknownWithoutReference", context.ProgramCompleteness);
        Assert.Equal(context.ContextRevision, (await Read(store.Db, Enumerable.Range(1, 14).Reverse().ToArray())).ContextRevision);
        var relatedIds = context.Related.Select(r => r.Track.Id).ToList(); var relatedCursor = context.NextCursor;
        while (relatedCursor is not null)
        {
            var more = await Read(store.Db, Enumerable.Range(1, 14).ToArray(), cursor: relatedCursor, limit: 100);
            Assert.Equal(context.ContextRevision, more.ContextRevision); relatedIds.AddRange(more.Related.Select(r => r.Track.Id)); relatedCursor = more.NextCursor;
        }
        Assert.Equal(4986, relatedIds.Distinct().Count());
        var before = JsonSerializer.Serialize(await store.Db.Songs.AsNoTracking().Where(s => s.Id != 1).OrderBy(s => s.Id).ToArrayAsync());
        var legacy = await AlbumReviewEndpoints.ReadContext(new(["/synthetic/flat"], [1]), store.Db, User, Files, default);
        Assert.Equal(413, Assert.IsAssignableFrom<IStatusCodeHttpResult>(legacy).StatusCode);
        var preview = await ReviewPlanService.PreviewAsync(store.Db, Files, Resolver, Owner, Plan(context), default);
        Assert.True(preview.CanApply);
        var applied = await ReviewPlanService.ApplyAsync(store.Db, Files, Resolver, Owner, preview.Id, preview.Revision, default);
        Assert.Equal("metadataApplied", applied.State); Assert.NotNull(applied.JobId);
        Assert.Equal(before, JsonSerializer.Serialize(await store.Db.Songs.AsNoTracking().Where(s => s.Id != 1).OrderBy(s => s.Id).ToArrayAsync()));
        var group = Assert.Single((await AlbumAuditReader.InventoryAsync(store.Db, Owner, null, null, 25, null, default)).Groups);
        Assert.False(group.MembersComplete); var memberIds = group.MemberIds.ToList(); var cursor = group.MemberCursor;
        while (cursor is not null) { var more = await AlbumAuditReader.MembersAsync(store.Db, Owner, cursor, default); memberIds.AddRange(more.MemberIds); cursor = more.NextCursor; }
        Assert.Equal(5000, memberIds.Distinct().Count());
    }

    [PostgresFact]
    public async Task BlockingNeighborsAreAlwaysVisible_EvenOutsideRelatedPage_AndOverflowDisablesPlan()
    {
        await using var store = await Store.CreateAsync();
        store.Db.AddRange(Enumerable.Range(1, 40).Select(i => Song(i, i is 1 or 40 ? "Program" : $"Other {i}")));
        store.Db.Songs.Local.Single(s => s.Id == 40).EnrichmentStatus = EnrichmentStatus.Pending;
        await store.Db.SaveChangesAsync();
        var context = await Read(store.Db, [1], limit: 2);
        Assert.True(context.PlanReady); Assert.Equal(40, Assert.Single(context.BlockingRelated).Id);
        Assert.DoesNotContain(context.Related, r => r.Track.Id == 40); Assert.Equal(1, context.BlockingRelatedCount);
        store.Db.AddRange(Enumerable.Range(41, 201).Select(i => Song(i))); await store.Db.SaveChangesAsync();
        var tooWide = await Read(store.Db, [1]); Assert.False(tooWide.PlanReady); Assert.Null(tooWide.PlanContext);
        Assert.Equal(202, tooWide.BlockingRelatedCount);
    }

    [PostgresFact]
    public async Task CachedReferenceMissingPosition_IsSeparateFromDuplicateSlot_AndRemainsHypothesis()
    {
        await using var store = await Store.CreateAsync();
        store.Db.AddRange(Enumerable.Range(1, 13).Select(i => Song(i)));
        var canonical = new CanonicalAlbum { ArtistKey = "example ensemble", AlbumKey = "program", DisplayArtist = "Example Ensemble", DisplayTitle = "Program", Status = CanonicalAlbumStatus.Fetched, ResolvedTrackCount = 14, FetchedAtUtc = Stamp,
            Tracks = Enumerable.Range(1, 14).Select(i => new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = i, Title = $"Movement {i}", DurationMs = 120000 }).ToList() };
        store.Db.Add(canonical); await store.Db.SaveChangesAsync();
        var initial = await Read(store.Db, Enumerable.Range(1, 13).ToArray()); var reference = Assert.Single(initial.References).Reference;
        var context = await Read(store.Db, Enumerable.Range(1, 13).ToArray(), reference);
        Assert.Contains(context.Facts, f => f.Kind == "uncoveredReferencePosition" && f.TrackNumber == 14);
        Assert.Equal(13, context.Correspondences.Count); Assert.Equal("referenceHypothesisOnly", context.ProgramCompleteness);
        var neighbor = Song(14); neighbor.EnrichmentStatus = EnrichmentStatus.Pending; store.Db.Add(neighbor); await store.Db.SaveChangesAsync();
        var withInbox = await Read(store.Db, Enumerable.Range(1, 13).ToArray(), reference);
        Assert.Equal(14, Assert.Single(withInbox.BlockingRelated).Id); Assert.Equal(13, withInbox.Members.Count);
        Assert.Equal(reference, Assert.Single(withInbox.References).Reference);
        Assert.Equal(14, (await Read(store.Db, Enumerable.Range(1, 14).ToArray(), reference)).Members.Count);
        await store.Db.Songs.Where(s => s.Id == 8).ExecuteUpdateAsync(s => s.SetProperty(x => x.TrackNumber, 7));
        var duplicate = await Read(store.Db, Enumerable.Range(1, 14).ToArray(), reference);
        Assert.Contains(duplicate.Facts, f => f.Kind == "albumPosition");
        Assert.Contains(duplicate.Facts, f => f.Kind == "uncoveredReferencePosition" && f.TrackNumber == 8);
        var referencePlanRequest = Plan(await Read(store.Db, Enumerable.Range(1, 14).ToArray(), reference));
        var referencePlan = await ReviewPlanService.PreviewAsync(store.Db, Files, Resolver, Owner, referencePlanRequest, default);
        await store.Db.CanonicalAlbums.Where(a => a.Id == canonical.Id).ExecuteUpdateAsync(a => a.SetProperty(x => x.TrackCountContested, true));
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.PreviewAsync(store.Db, Files, Resolver, Owner, referencePlanRequest, default))).Error.Code);
        Assert.Equal("plan_stale", (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.ApplyAsync(store.Db, Files, Resolver, Owner, referencePlan.Id, referencePlan.Revision, default))).Error.Code);
        var changed = await AlbumAuditEndpoints.Context(new([1], reference), store.Db, User, Files, default);
        Assert.Equal("reference_changed", Value<AlbumReviewError>(changed).Code);
    }

    [PostgresTheory]
    [InlineData(null, 1, "possibleAlbumPosition")]
    [InlineData(1, 1, "albumPosition")]
    [InlineData(1, 2, null)]
    public async Task MultiFromSelection_PreservesUnknownDiscs_AndDistinctMemberships(int? disc1, int? disc2, string? conflict)
    {
        await using var store = await Store.CreateAsync();
        var first = Song(1, "Program", "/disc-a"); var second = Song(2, "Program", "/disc-b");
        first.DiscNumber = disc1; second.DiscNumber = disc2; second.TrackNumber = 1;
        first.MusicBrainzId = second.MusicBrainzId = "same-recording"; second.IsDuplicate = true;
        first.DestinationPath = "/library/CD1/1.flac"; second.DestinationPath = "/library/CD2/1.flac";
        store.Db.AddRange(first, second); await store.Db.SaveChangesAsync();
        var context = await Read(store.Db, [2, 1]); Assert.Equal(2, context.Members.Count);
        Assert.Equal(disc1, context.Members[0].Current.Values["DiscNumber"]);
        Assert.Equal(conflict, context.Conflicts.SingleOrDefault()?.Kind);
        Assert.Equal("notCaptured", context.Members[0].Original.Provenance);
        Assert.Empty(context.Members[0].Original.Values); Assert.True(context.Members[1].IsDuplicate);
    }

    [PostgresTheory]
    [InlineData("member")]
    [InlineData("related")]
    [InlineData("newPending")]
    [InlineData("newUnknown")]
    public async Task DependencyDriftRejectsOldPreviewAndApply_AndUnrelatedAlbumsDoNot(string kind)
    {
        await using var store = await Store.CreateAsync(); store.Db.AddRange(Song(1), Song(2, "Other")); await store.Db.SaveChangesAsync();
        var context = await Read(store.Db, [1]); var request = Plan(context);
        var preview = await ReviewPlanService.PreviewAsync(store.Db, Files, Resolver, Owner, request, default);
        store.Db.Add(Song(3, "Distinct album", "/elsewhere")); await store.Db.SaveChangesAsync();
        Assert.Equal(context.ContextRevision, (await Read(store.Db, [1])).ContextRevision);
        if (kind == "member") await store.Db.Songs.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "Changed"));
        if (kind == "related") await store.Db.Songs.Where(s => s.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(x => x.DiscNumber, (int?)null));
        if (kind == "newPending") { var pending = Song(4); pending.EnrichmentStatus = EnrichmentStatus.Pending; store.Db.Add(pending); await store.Db.SaveChangesAsync(); }
        if (kind == "newUnknown") { var unknown = Song(4); unknown.Artist = unknown.AlbumArtist = unknown.Album = null; store.Db.Add(unknown); await store.Db.SaveChangesAsync(); }
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.PreviewAsync(store.Db, Files, Resolver, Owner, request, default))).Error.Code);
        Assert.Equal("plan_stale", (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.ApplyAsync(store.Db, Files, Resolver, Owner, preview.Id, preview.Revision, default))).Error.Code);
    }

    [PostgresFact]
    public async Task PendingCannotBeWritten_NewIdentityNeedsExpandedContext_AndForeignSelectionIsAtomic()
    {
        await using var store = await Store.CreateAsync(); var pending = Song(2, "Pending program"); pending.EnrichmentStatus = EnrichmentStatus.Pending;
        var foreign = Song(3); foreign.OwnerUserId = TestUsers.DemoId;
        store.Db.AddRange(Song(1), pending, foreign, Song(4, "New program", "/elsewhere")); await store.Db.SaveChangesAsync();
        var selected = await Read(store.Db, [2]);
        Assert.Equal("invalid_track_state", (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.PreviewAsync(store.Db, Files, Resolver, Owner, Plan(selected, 2), default))).Error.Code);
        var context = await Read(store.Db, [1]); var plan = Plan(context);
        plan.Tracks[0].Fields.Add("Album", JsonSerializer.SerializeToElement("New program"));
        Assert.Equal("expand_context", (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.PreviewAsync(store.Db, Files, Resolver, Owner, plan, default))).Error.Code);
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await AlbumAuditEndpoints.Context(new([1, 3]), store.Db, User, Files, default)).StatusCode);
        Assert.Equal(413, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await AlbumAuditEndpoints.Context(new(Enumerable.Range(1, 201).ToArray()), store.Db, User, Files, default)).StatusCode);
        Assert.Empty(await store.Db.ReviewPlans.ToArrayAsync());
    }

    [PostgresFact]
    public async Task ContextAuthAndReferenceFailures_DoNotDiscloseEvidence()
    {
        await using var store = await Store.CreateAsync(); store.Db.Add(Song(1)); await store.Db.SaveChangesAsync();
        Assert.Equal(401, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await AlbumAuditEndpoints.Context(new([1]), store.Db, new TestCurrentUserAccessor(), Files, default)).StatusCode);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>(await AlbumAuditEndpoints.Context(new([1]), store.Db, new TestCurrentUserAccessor(TestCurrentUserAccessor.FriendUser), Files, default));
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await AlbumAuditEndpoints.Context(new([1], new("url", 1, "x")), store.Db, User, Files, default)).StatusCode);
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await AlbumAuditEndpoints.Context(new([1], new("canonicalAlbum", 999, "x")), store.Db, User, Files, default)).StatusCode);
    }
    [Fact]
    public void LegacyScopeJsonRemainsByteCompatible_AndCursorTamperingIsRejected()
    {
        Assert.Equal("{\"froms\":[\"/source\"],\"targetSongIds\":[1]}", JsonSerializer.Serialize(new AlbumReviewContextRequest(["/source"], [1]), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [PostgresFact]
    public async Task RelatedContinuationsAreStable_AndEvidenceChangesInvalidateUnreadPages()
    {
        await using var store = await Store.CreateAsync(); store.Db.AddRange(Enumerable.Range(1, 8).Select(i => Song(i, $"Other {i}"))); await store.Db.SaveChangesAsync();
        var first = await Read(store.Db, [1], limit: 2); var second = await Read(store.Db, [1], cursor: first.NextCursor, limit: 2);
        Assert.Equal(first.ContextRevision, second.ContextRevision); Assert.Equal(new[] { 4, 5 }, second.Related.Select(r => r.Track.Id));
        var cursor = first.NextCursor!; var corrupted = cursor[..^5] + (cursor[^5] == 'A' ? "B" : "A") + cursor[^4..];
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await AlbumAuditEndpoints.Context(new([1], Cursor: corrupted), store.Db, User, Files, default)).StatusCode);
        store.Db.SongMetadataChanges.Add(new() { SongId = 8, FieldName = "Title", NewValue = "Changed historical evidence", Source = "fixture", CreatedAtUtc = Stamp }); await store.Db.SaveChangesAsync();
        var stale = await AlbumAuditEndpoints.Context(new([1], Cursor: first.NextCursor), store.Db, User, Files, default);
        Assert.Equal("context_changed", Value<AlbumReviewError>(stale).Code);
    }

    [PostgresFact]
    public async Task ExplicitUnknownAndPostgresPrecisionSurviveFreshContexts_RealMicrosecondChangesInvalidate()
    {
        await using var store = await Store.CreateAsync(); var song = Song(1); song.DiscNumber = null; song.IsManuallyApproved = true; song.ManuallyApprovedAtUtc = Stamp.AddTicks(1234567);
        store.Db.Add(song); store.Db.SongMetadataChanges.Add(new() { SongId = 1, FieldName = "DiscNumber", NewValue = null, Source = "ManualReview", CreatedAtUtc = song.ManuallyApprovedAtUtc.Value, AppliedAtUtc = song.ManuallyApprovedAtUtc }); await store.Db.SaveChangesAsync();
        var first = await Read(store.Db, [1]);
        await using var fresh = new MusicHoarderDbContext(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString, p => p.EnableRetryOnFailure()).Options);
        var next = await Read(fresh, [1]); Assert.Equal(first.ContextRevision, next.ContextRevision);
        Assert.Equal("explicitUnknown", Assert.Single(next.Members[0].ManualFields).State); Assert.Null(next.Members[0].Current.Values["DiscNumber"]);
        await fresh.Songs.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.ManuallyApprovedAtUtc, ReviewTimestamp.Persisted(song.ManuallyApprovedAtUtc)!.Value.AddTicks(10)));
        Assert.NotEqual(first.ContextRevision, (await Read(fresh, [1])).ContextRevision);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenceBudgetsFailExplicitly_WithoutPartialPacket(bool byteBudget)
    {
        await using var store = await Store.CreateAsync(); store.Db.Add(Song(1));
        store.Db.SongMetadataChanges.AddRange(Enumerable.Range(1, byteBudget ? 700 : 4001).Select(i => new SongMetadataChange
        { SongId = 1, FieldName = "Title", Source = "fixture", NewValue = byteBudget ? new string('x', 4096) : "Example", CreatedAtUtc = Stamp }));
        await store.Db.SaveChangesAsync();
        var result = await AlbumAuditEndpoints.Context(new([1]), store.Db, User, Files, default);
        Assert.Equal(413, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode); Assert.Equal("evidence_too_large", Value<AlbumReviewError>(result).Code);
    }

    [PostgresFact]
    public async Task CompilationsBestOfLiveAndBonusRemainExplicitPrograms_ReferencesExposeUncertainty()
    {
        await using var store = await Store.CreateAsync();
        var compilation = Enumerable.Range(1, 12).Select(i => { var s = Song(i, "Various collection", "/compilation"); s.AlbumArtist = "Various Artists"; s.Artist = $"Guest {i}"; s.IsCompilation = true; return s; }).ToArray();
        var bestOf = Enumerable.Range(13, 10).Select(i => Song(i, "Best Of", "/best-of")).ToArray();
        var studio = Song(23, "Program", "/studio"); studio.TrackNumber = 1; studio.MusicBrainzId = "recording-shared";
        compilation[0].MusicBrainzId = studio.MusicBrainzId; compilation[0].IsDuplicate = true; compilation[0].DuplicateOfId = 23;
        var live = Song(24, "Program", "/live"); live.Title = studio.Title; live.TrackNumber = 2; live.DurationMs = 300000; live.ReleaseTypePrimary = "Live";
        store.Db.AddRange(compilation); store.Db.AddRange(bestOf); store.Db.AddRange(studio, live);
        var reference = new CanonicalAlbum { ArtistKey = "example ensemble", AlbumKey = "program", DisplayTitle = "Program", DisplayArtist = "Example Ensemble", Status = CanonicalAlbumStatus.Fetched, TrackCountContested = true, ResolvedTrackCount = 14,
          SourcesJson = "[{\"Provider\":0,\"AlbumId\":\"fixture:standard\",\"TrackCount\":14,\"InWinningCluster\":true},{\"Provider\":1,\"AlbumId\":\"fixture:bonus\",\"TrackCount\":16,\"InWinningCluster\":true}]",
          Tracks = [new() { DiscNumber = 1, TrackNumber = 1, DurationMs = 120000 }, new() { DiscNumber = 1, TrackNumber = 2, DurationMs = 120000 }, new() { DiscNumber = 1, TrackNumber = 15, IsContested = true }] };
        store.Db.Add(reference); await store.Db.SaveChangesAsync();
        var va = await Read(store.Db, compilation.Select(s => s.Id).ToArray()); Assert.Equal(12, va.Members.Count); Assert.All(va.Members, s => Assert.Equal(true, s.Current.Values["IsCompilation"]));
        Assert.Equal(23, Assert.Single(va.BlockingRelated, s => s.Id == 23).Id); Assert.True(va.Members[0].IsDuplicate);
        var best = await Read(store.Db, bestOf.Select(s => s.Id).ToArray()); Assert.Equal(10, best.Members.Count); Assert.All(best.Members, s => Assert.Equal(false, s.Current.Values["IsCompilation"]));
        var initial = await Read(store.Db, [23, 24]); var cached = Assert.Single(initial.References);
        var context = await Read(store.Db, [23, 24], cached.Reference); Assert.Equal("contested", context.ReferenceState);
        Assert.Contains(context.Facts, f => f.Kind == "durationContradiction" && f.SongIds.Contains(24));
        Assert.Contains(context.Facts, f => f.Kind == "contradictoryReleaseTypePrimary"); Assert.Contains(context.Facts, f => f.Kind == "uncoveredReferencePosition" && f.TrackNumber == 15);
        Assert.Equal(2, context.Members.Count); Assert.Equal(2, cached.Program.Sources.Count);
    }

    [PostgresFact]
    public async Task HttpAndOpenApiExposeNewScopes_AndSnapshotRetriesRemainReadOnly()
    {
        await using var store = await Store.CreateAsync(); store.Db.Add(Song(1)); await store.Db.SaveChangesAsync();
        var probe = new SnapshotRetry();
        await using (var db = new MusicHoarderDbContext(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString, p => p.EnableRetryOnFailure()).AddInterceptors(probe).Options))
        {
            var result = await AlbumAuditEndpoints.Context(new([1]), db, User, Files, default);
            Assert.Single(Value<AlbumAuditContext>(result).Members); Assert.True(probe.Retried); Assert.Equal(2, probe.Snapshots);
            Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddDbContext<MusicHoarderDbContext>(o => o.UseNpgsql(store.ConnectionString, p => p.EnableRetryOnFailure()));
        builder.Services.AddSingleton<ICurrentUserAccessor>(User); builder.Services.AddSingleton<IFileSystem>(Files);
        builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()); builder.Services.AddSingleton<AlbumAuditCursorCodec>(); builder.Services.AddOpenApi();
        await using var app = builder.Build(); app.MapAlbumReviewEndpoints(); app.MapOpenApi(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var inventory = await client.GetFromJsonAsync<AlbumAuditInventory>("/api/review/album-audit/groups?limit=1"); Assert.Single(inventory!.Groups);
        using var response = await client.PostAsJsonAsync("/api/review/album-audit/context", new AlbumAuditContextRequest([1])); response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadFromJsonAsync<AlbumAuditContext>())!.PlanReady);
        var openApi = await client.GetStringAsync("/openapi/v1.json");
        foreach (var text in new[] { "/api/review/album-audit/groups", "/api/review/album-audit/group-members", "/api/review/album-audit/context", "AlbumAuditScope", "AlbumAuditContext", "409", "413" }) Assert.Contains(text, openApi);
        var evidenceRoot = Environment.GetEnvironmentVariable("MH_AUDIT_TEST_EVIDENCE");
        if (!string.IsNullOrEmpty(evidenceRoot)) { Directory.CreateDirectory(evidenceRoot); await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "e-openapi.json"), openApi); }
        await app.StopAsync();
    }

    private sealed class SnapshotRetry : DbCommandInterceptor
    {
        internal bool Retried { get; private set; }
        internal int Snapshots { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        { if (command.CommandText == "SET TRANSACTION READ ONLY") Snapshots++; return ValueTask.FromResult(result); }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Assert.Equal(System.Data.IsolationLevel.RepeatableRead, command.Transaction!.IsolationLevel);
            using var check = command.Connection!.CreateCommand(); check.Transaction = command.Transaction; check.CommandText = "SELECT current_setting('transaction_read_only')";
            Assert.Equal("on", await check.ExecuteScalarAsync(ct));
            if (!Retried) { Retried = true; throw new Npgsql.NpgsqlException("Synthetic transient read failure", new TimeoutException("Synthetic timeout")); }
            return result;
        }
    }

    private sealed class SqlTrace : DbCommandInterceptor
    {
        internal List<string> Statements { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Statements.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

}
