using System.Net.Http.Json;
using System.Data;
using System.Data.Common;
using System.IO.Abstractions;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.Middleware;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Tests.Auth;
using Npgsql;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>Real PostgreSQL + synthetic rows + temporary copies, no production startup/workers.
/// Set MH_REVIEW_TEST_POSTGRES to a disposable local server, Database=mh_review_test.
/// Each test creates and drops its own random database; an absent variable yields an explicit skip.</summary>
public sealed class AlbumReviewContextTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TestCurrentUserAccessor Owner = new(TestCurrentUserAccessor.OwnerUser);

    [PostgresFact]
    public async Task ProductionRetryStrategy_ReplaysWholeReadOnlySnapshot_WithoutWrites_AndHonorsCancellation()
    {
        await using var store = await TestDatabase.CreateAsync();
        store.Db.Songs.Add(Song(1, "/source/album"));
        await store.Db.SaveChangesAsync();
        var before = await Snapshot(store.Db);
        var probe = new RetrySnapshotProbe();
        await using var db = new MusicHoarderDbContext(new DbContextOptionsBuilder()
            .UseNpgsql(store.ConnectionString, pg => pg.EnableRetryOnFailure())
            .AddInterceptors(probe).Options, Owner);
        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);
        probe.Armed = true;

        var packet = await Read(db, new(["/source/album"]));

        probe.Armed = false;
        Assert.Single(packet.Tracks);
        Assert.True(probe.InjectedTransientFailure);
        Assert.Equal(2, probe.ReadOnlySetCount);
        Assert.True(probe.Snapshots.Count >= 2);
        Assert.All(probe.Snapshots, s =>
        {
            Assert.Equal(IsolationLevel.RepeatableRead, s.Isolation);
            Assert.Equal("on", s.ReadOnly);
        });
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(packet.Revision, (await Read(db, new(["/source/album"]))).Revision);
        Assert.Equal(before, await Snapshot(store.Db));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AlbumReviewEndpoints.ReadContext(
            new(["/source/album"]), db, Owner, new FileSystem(), cancelled.Token));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(before, await Snapshot(store.Db));
    }

    [PostgresTheory]
    [InlineData(null, 1, false, "possibleAlbumPosition")]
    [InlineData(1, null, false, "possibleAlbumPosition")]
    [InlineData(null, null, false, "possibleAlbumPosition")]
    [InlineData(1, 1, false, "albumPosition")]
    [InlineData(2, 2, false, "albumPosition")]
    [InlineData(1, 2, false, null)]
    [InlineData(null, 2, false, null)]
    [InlineData(null, 1, true, null)]
    [InlineData(null, null, true, null)]
    [InlineData(1, 1, true, null)]
    public async Task PositionWarnings_RespectBuilderSlots_WithoutInventingDiscMetadata(
        int? firstDisc, int? secondDisc, bool differentProgram, string? expectedKind)
    {
        await using var store = await TestDatabase.CreateAsync();
        var db = store.Db;
        var first = Song(1, "/source/album", track: 20, disc: firstDisc);
        var second = Song(2, "/source/album", track: 20, disc: secondDisc);
        first.Title = second.Title = "Example take";
        first.DurationMs = 111000;
        second.DurationMs = 124000;
        second.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        if (differentProgram) second.Album = "Another Program";
        foreach (var song in new[] { first, second })
        {
            song.OriginalMetadataCaptured = true;
            song.OriginalDiscNumber = song.DiscNumber;
        }
        var resolver = new DestinationPathResolver(Microsoft.Extensions.Options.Options.Create(
            new MusicEnricherOptions { DestinationDirectory = "/destination" }));
        first.DestinationPath = resolver.ResolvePath(first);
        first.LibraryBuildStatus = LibraryBuildStatus.Done;
        var firstFolder = Path.GetDirectoryName(first.DestinationPath)!;
        var secondFolder = Path.GetDirectoryName(resolver.ResolvePath(second))!;
        // Exercise the existing guard's actual slot key without running its builder or mutations.
        var nativeKey = typeof(LibraryBuilderService).GetMethod("PositionKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var firstKey = nativeKey.Invoke(null, new object?[] { TestUsers.OwnerId, firstFolder, firstDisc, 20 });
        var secondKey = nativeKey.Invoke(null, new object?[] { TestUsers.OwnerId, secondFolder, secondDisc, 20 });
        Assert.Equal(expectedKind is not null, Equals(firstKey, secondKey));
        db.Songs.AddRange(first, second);
        await db.SaveChangesAsync();
        var before = await Snapshot(db);

        var packet = await Read(db, new(["/source/album"], [2]));

        Assert.Equal(new[] { 2 }, packet.Scope.TargetSongIds);
        Assert.Equal(2, packet.Tracks.Count);
        foreach (var (id, disc) in new[] { (1, firstDisc), (2, secondDisc) })
        {
            var track = packet.Tracks.Single(t => t.Id == id);
            Assert.Equal(disc, (int?)track.Current.Values["DiscNumber"]);
            Assert.Equal(disc, (int?)track.Original.Values["DiscNumber"]);
            Assert.False(track.IsDuplicate);
        }
        if (expectedKind is null) Assert.Empty(packet.Conflicts);
        else
        {
            var conflict = Assert.Single(packet.Conflicts);
            Assert.Equal(expectedKind, conflict.Kind);
            Assert.Equal(new[] { 1, 2 }, conflict.SongIds);
            using var key = JsonDocument.Parse(conflict.Key);
            var disc = key.RootElement.GetProperty("disc");
            if (firstDisc is null || secondDisc is null) Assert.Equal(JsonValueKind.Null, disc.ValueKind);
            else Assert.Equal(firstDisc, disc.GetInt32());
        }
        Assert.Equal(before, await Snapshot(db));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    [PostgresFact]
    public async Task ThirteenMatchedPlusOneReview_HasWholeContextConflictAndContradictoryEvidence_WithoutWrites()
    {
        await using var store = await TestDatabase.CreateAsync();
        var db = store.Db;
        for (var i = 1; i <= 14; i++) db.Songs.Add(Song(i, "/source/program", track: i == 14 ? 13 : i));
        db.Songs.Local.Single(s => s.Id == 14).EnrichmentStatus = EnrichmentStatus.NeedsReview;
        db.Songs.Local.Single(s => s.Id == 14).OriginalMetadataCaptured = true;
        db.Songs.Local.Single(s => s.Id == 14).OriginalTitle = "Original take";
        db.Songs.Local.Single(s => s.Id == 14).OriginalDiscNumber = 2;
        db.Songs.Local.Single(s => s.Id == 14).OriginalTrackNumber = 14;
        db.Songs.Local.Single(s => s.Id == 14).MusicBrainzReleaseId = "contradictory-release";
        db.Songs.Local.Single(s => s.Id == 14).PlainLyrics = "PRIVATE_LYRICS_CANARY";
        db.Songs.Local.Single(s => s.Id == 14).Fingerprint = "PRIVATE_FINGERPRINT_CANARY";
        db.SongProviderAttempts.AddRange(Attempt(1, 14, "Radio edit", 180), Attempt(2, 14, "Extended mix", 350));
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "example ensemble", AlbumKey = "example program", DisplayArtist = "Example Ensemble",
            DisplayTitle = "Example Program", Status = CanonicalAlbumStatus.Fetched, ResolvedTrackCount = 14, FetchedAtUtc = Stamp,
            SourcesJson = """[{"Provider":2,"AlbumId":"release-example","TrackCount":14,"InWinningCluster":true,"Secret":"SOURCE_CANARY"}]""",
            Tracks = Enumerable.Range(1, 14).Select(i => new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = i, Title = $"Movement {i}", DurationMs = 180000 }).ToArray()
        });
        await db.SaveChangesAsync();
        var before = await Snapshot(db);
        var packet = await Read(db, new(["/source/program"], [14]));
        Assert.Equal(14, packet.Tracks.Count);
        Assert.Equal(new[] { 14 }, packet.Scope.TargetSongIds);
        Assert.Equal(13, packet.Tracks.Count(t => t.EnrichmentStatus == "Matched"));
        Assert.Contains(packet.Conflicts, c => c.Kind == "possibleAlbumPosition" && c.SongIds.SequenceEqual(new[] { 13, 14 }));
        var target = packet.Tracks.Single(t => t.Id == 14);
        Assert.Equal("Original take", target.Original.Values["Title"]);
        Assert.Equal(2, target.Original.Values["DiscNumber"]);
        Assert.False(target.Original.Values.ContainsKey("MusicBrainzReleaseId"));
        Assert.Equal(new[] { 180, 350 }, target.Candidates.Select(c => c.DurationSeconds!.Value));
        Assert.Equal(new[] { "Radio edit", "Extended mix" }, target.Candidates.Select(c => c.Tags!.Values["Title"]));
        Assert.All(target.Candidates, c => Assert.Equal(Stamp, c.CollectedAtUtc));
        Assert.False(packet.Scope.ProgramEstablished);
        Assert.Equal(14, Assert.Single(packet.Programs).Tracks.Count);
        Assert.Equal("normalizedNameCacheOnly", packet.Programs[0].Relation);
        var payload = JsonSerializer.Serialize(packet);
        Assert.DoesNotContain("CANARY", payload);
        Assert.Equal(before, await Snapshot(db));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    [PostgresFact]
    public async Task Search_FiltersGroupsWithoutLosingMatchedNeighbors_PagesAndPrioritizesRemaining()
    {
        await using var store = await TestDatabase.CreateAsync();
        var db = store.Db;
        db.Songs.AddRange(Song(1, "/source/near"), Song(2, "/source/near"), Song(3, "/source/near"),
            Song(4, "/source/later"), Song(5, "/source/done"), Song(6, "/source/later"));
        db.Songs.Local.Single(s => s.Id == 3).EnrichmentStatus = EnrichmentStatus.NeedsReview;
        db.Songs.Local.Single(s => s.Id == 4).EnrichmentStatus = EnrichmentStatus.Pending;
        db.Songs.Local.Single(s => s.Id == 6).EnrichmentStatus = EnrichmentStatus.Failed;
        db.Songs.Local.Single(s => s.Id == 3).OriginalTitle = "Find original";
        db.Songs.Add(Song(20, "/foreign/secret", owner: TestUsers.DemoId));
        await db.SaveChangesAsync();
        var first = await Search(db, limit: 1);
        Assert.Equal("/source/near", Assert.Single(first.Groups).From);
        Assert.Equal(1, first.NextOffset);
        var second = await Search(db, offset: 1, limit: 1);
        Assert.Equal("/source/later", Assert.Single(second.Groups).From);
        var last = await Search(db, offset: 2, limit: 1);
        Assert.Equal("/source/done", Assert.Single(last.Groups).From);
        Assert.Null(last.NextOffset);
        var filtered = await AlbumReviewEndpoints.Search(db, Owner, default, from: "/source/near", query: "original", status: "NeedsReview", maxRemaining: 1);
        var group = Assert.Single(Value<AlbumReviewGroupsPage>(filtered).Groups);
        Assert.Equal(3, group.TrackCount);
        Assert.Equal(2, group.MatchedCount);
        Assert.Equal(1, group.RemainingCount);
        Assert.Empty((await Search(db, offset: 10)).Groups);
        var injection = await Search(db, query: "' OR true --");
        Assert.Empty(injection.Groups);
    }

    [PostgresFact]
    public async Task ExplicitSiblingCdSelection_PreservesDiscs_AndDoesNotSelectTheOtherFlatFolderMembers()
    {
        await using var store = await TestDatabase.CreateAsync();
        var db = store.Db;
        db.Songs.AddRange(Song(1, "/source/box/CD1", disc: 1), Song(2, "/source/box/CD2", disc: 2),
            Song(3, "/source/box/CD20", disc: 20), Song(4, "/source/flat"), Song(5, "/source/flat"));
        db.Songs.Local.Single(s => s.Id == 3).Album = "Unrelated";
        db.Songs.Local.Single(s => s.Id == 5).Album = "Other program";
        await db.SaveChangesAsync();
        var one = await Read(db, new(["/source/box/CD1"]));
        Assert.Equal(new[] { 1 }, one.Scope.TargetSongIds);
        Assert.Contains(one.Tracks, t => t.Id == 2 && !t.ContextReasons.Contains("selected"));
        Assert.DoesNotContain(one.Tracks, t => t.Id == 3);
        var both = await Read(db, new(["/source/box/CD1", "/source/box/CD2"]));
        Assert.Equal(new[] { 1, 2 }, both.Scope.TargetSongIds);
        Assert.Equal(new[] { 1, 2 }, both.Tracks.Where(t => t.Id <= 2).Select(t => (int)t.Current.Values["DiscNumber"]!));
        var flat = await Read(db, new(["/source/flat"], [4]));
        Assert.Contains(flat.Tracks, t => t.Id == 5 && t.ContextReasons.Contains("sameFrom"));
        Assert.Equal(new[] { 4 }, flat.Scope.TargetSongIds);
        Assert.False(flat.Scope.ProgramEstablished);
    }

    [PostgresFact]
    public async Task DuplicateGraph_IncludesDismissalsAndLegacyKeepers_ButNeverForeignOrDeletedReferences()
    {
        await using var store = await TestDatabase.CreateAsync();
        var db = store.Db;
        db.Songs.AddRange(Song(1, "/source/start"), Song(2, "/source/neighbor"), Song(3, "/source/legacy"),
            Song(4, "/foreign/private", owner: TestUsers.DemoId), Song(5, "/source/deleted"));
        foreach (var song in db.Songs.Local) song.Album = $"Separate {song.Id}";
        db.Songs.Local.Single(s => s.Id == 1).DuplicateOfId = 3;
        db.Songs.Local.Single(s => s.Id == 5).DeletedAtUtc = Stamp;
        db.SongDuplicateLinks.AddRange(Link(1, 1, 2, DuplicateLinkStatus.Dismissed), Link(2, 2, 4), Link(3, 2, 5));
        await db.SaveChangesAsync();
        var packet = await Read(db, new(["/source/start"]));
        Assert.Equal(new[] { 1, 2, 3 }, packet.Tracks.Select(t => t.Id));
        Assert.Equal("Dismissed", Assert.Single(packet.Duplicates).Status);
        Assert.Equal(3, packet.Tracks.Single(t => t.Id == 1).DuplicateOfId);
        var foreign = await AlbumReviewEndpoints.ReadContext(new(["/source/start"], [1, 4]), db, Owner, new FileSystem(), default);
        Assert.Equal(404, Status(foreign));
        var deleted = await AlbumReviewEndpoints.ReadContext(new(["/source/deleted"]), db, Owner, new FileSystem(), default);
        Assert.Equal(404, Status(deleted));
    }

    [PostgresFact]
    public async Task ManualUnknownAndOriginalAbsent_AreSeparateFromNullCurrentAndInactiveAudits()
    {
        await using var store = await TestDatabase.CreateAsync();
        var db = store.Db;
        var song = Song(1, "/source/program");
        song.IsManuallyApproved = true;
        song.ManuallyApprovedAtUtc = Stamp;
        db.Songs.Add(song);
        db.SongMetadataChanges.AddRange(Change(1, 1, "Year", null, Stamp), Change(2, 1, "Title", song.Title, Stamp),
            Change(3, 1, "Album", "Stale approval", Stamp.AddDays(-1)),
            new SongMetadataChange { Id = 4, SongId = 1, FieldName = "Title", NewValue = "Proposed", Source = "consensus", CreatedAtUtc = Stamp },
            new SongMetadataChange { Id = 5, SongId = 1, FieldName = "PlainLyrics", NewValue = "PRIVATE_CANARY", Source = "ManualReview", CreatedAtUtc = Stamp });
        db.SongProviderAttempts.Add(new SongProviderAttempt { SongId = 1, AttemptedAtUtc = Stamp, MatchedDataJson = "{bad", Error = "SECRET_CANARY" });
        await db.SaveChangesAsync();
        var track = Assert.Single((await Read(db, new(["/source/program"]))).Tracks);
        Assert.Equal("notCaptured", track.Original.Provenance);
        Assert.Empty(track.Original.Values);
        Assert.Null(track.Current.Values["Year"]);
        Assert.Equal("explicitUnknown", track.ManualFields.Single(f => f.Field == "Year").State);
        Assert.Equal("approvedValue", track.ManualFields.Single(f => f.Field == "Title").State);
        Assert.DoesNotContain(track.ManualFields, f => f.Field == "Album");
        Assert.Equal(4, track.Changes.Count);
        Assert.Equal("invalid", Assert.Single(track.Candidates).CandidateState);
        Assert.True(track.Candidates[0].HasError);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(track));
    }

    [PostgresFact]
    public async Task CopyPresenceAndRevision_ReflectFileAndNeighborChanges_WithoutClaimingVerification()
    {
        await using var store = await TestDatabase.CreateAsync();
        var db = store.Db;
        var folder = Path.Combine(Path.GetTempPath(), "mh-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var copy = Path.Combine(folder, "copy.mp3");
            await File.WriteAllTextAsync(copy, "synthetic-content");
            var song = Song(1, "/source/program");
            song.DestinationPath = copy;
            song.PreviousDestinationPath = Path.Combine(folder, "previous.mp3");
            song.LibraryBuildStatus = LibraryBuildStatus.Done;
            db.Songs.Add(song);
            await db.SaveChangesAsync();
            var a = await Read(db, new(["/source/program"]));
            var b = await Read(db, new(["/source/program"]));
            Assert.Equal(a.Revision, b.Revision);
            Assert.Equal("present", a.Tracks[0].Copy.Presence);
            Assert.Equal("unknown", a.Tracks[0].Copy.Verification);
            Assert.Equal("Done", a.Tracks[0].Copy.BuildStatus);
            Assert.Equal("missing", a.Tracks[0].Copy.Previous!.Presence);
            File.Delete(copy);
            var missing = await Read(db, new(["/source/program"]));
            Assert.Equal("missing", missing.Tracks[0].Copy.Presence);
            Assert.NotEqual(a.Revision, missing.Revision);
            song.Title = "Changed neighbor metadata";
            await db.SaveChangesAsync();
            Assert.NotEqual(missing.Revision, (await Read(db, new(["/source/program"]))).Revision);
        }
        finally { Directory.Delete(folder, true); }
    }

    [PostgresFact]
    public async Task LimitsAndInvalidSelection_ReturnExplicitErrors_NeverPartialEvidence()
    {
        await using var store = await TestDatabase.CreateAsync();
        var db = store.Db;
        db.Songs.AddRange(Enumerable.Range(1, 201).Select(i => Song(i, "/source/large")));
        await db.SaveChangesAsync();
        Assert.Equal(413, Status(await AlbumReviewEndpoints.ReadContext(new(["/source/large"]), db, Owner, new FileSystem(), default)));
        Assert.Equal(400, Status(await AlbumReviewEndpoints.ReadContext(new(["/source/large/"]), db, Owner, new FileSystem(), default)));
        Assert.Equal(400, Status(await AlbumReviewEndpoints.ReadContext(new(["/source/large"], []), db, Owner, new FileSystem(), default)));
        Assert.Equal(400, Status(await AlbumReviewEndpoints.Search(db, Owner, default, limit: 101)));
        Assert.Equal(400, Status(await AlbumReviewEndpoints.Search(db, Owner, default, status: "unknown")));
        Assert.Equal(401, Status(await AlbumReviewEndpoints.ReadContext(new(["/source/large"]), db, new TestCurrentUserAccessor(), new FileSystem(), default)));
        db.Songs.RemoveRange(db.Songs.Where(s => s.Id > 1));
        await db.SaveChangesAsync();
        db.SongMetadataChanges.AddRange(Enumerable.Range(1, 4001).Select(i => Change(i, 1, "Title", "Evidence", Stamp)));
        await db.SaveChangesAsync();
        Assert.Equal(413, Status(await AlbumReviewEndpoints.ReadContext(new(["/source/large"]), db, Owner, new FileSystem(), default)));
        db.SongMetadataChanges.RemoveRange(db.SongMetadataChanges);
        db.Songs.Local.Single().Title = new string('x', AlbumReviewContextReader.MaxResponseBytes);
        await db.SaveChangesAsync();
        var result = await AlbumReviewEndpoints.ReadContext(new(["/source/large"]), db, Owner, new FileSystem(), default);
        Assert.Equal(413, Status(result));
        Assert.Equal("response_limit", Value<AlbumReviewError>(result).Code);
    }

    [PostgresFact]
    public async Task HttpRoutesAndOpenApi_BindRequestsAndExposeTypedContracts_WithoutProductionWorkers()
    {
        await using var store = await TestDatabase.CreateAsync();
        store.Db.Songs.Add(Song(1, "/source/program"));
        await store.Db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        // The production Program uses this registration + Aspire enrichment, which enables retries.
        builder.Services.AddDbContext<MusicHoarderDbContext>(options => options.UseNpgsql(store.ConnectionString));
        builder.EnrichNpgsqlDbContext<MusicHoarderDbContext>();
        builder.Services.AddSingleton<ICurrentUserAccessor>(Owner);
        builder.Services.AddSingleton<IFileSystem, FileSystem>();
        builder.Services.AddOpenApi();
        await using var app = builder.Build();
        using (var scope = app.Services.CreateScope())
            Assert.True(scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>()
                .Database.CreateExecutionStrategy().RetriesOnFailure);
        app.MapAlbumReviewEndpoints();
        app.MapOpenApi();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var groups = await client.GetFromJsonAsync<AlbumReviewGroupsPage>("/api/review/album-groups?query=Example&limit=1");
        Assert.Single(groups!.Groups);
        using var response = await client.PostAsJsonAsync("/api/review/album-context", new AlbumReviewContextRequest(["/source/program"]));
        response.EnsureSuccessStatusCode();
        Assert.Single((await response.Content.ReadFromJsonAsync<AlbumReviewContext>())!.Tracks);
        var openApi = await client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("AlbumReviewContextRequest", openApi);
        Assert.Contains("AlbumReviewContext", openApi);
        Assert.Contains("/api/review/album-groups", openApi);
        Assert.Contains("413", openApi);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("POST", "/api/review/album-context", true)]
    [InlineData("POST", "/api/review/album-context/", true)]
    [InlineData("DELETE", "/api/review/album-context", false)]
    [InlineData("POST", "/api/review/album-context/apply", false)]
    public async Task ReadOnlyPost_TraversesMemberAndDemoGuards_WithoutOpeningNeighborRoutes(string method, string path, bool expected)
    {
        foreach (var user in new[] { TestCurrentUserAccessor.FriendUser, TestCurrentUserAccessor.DemoUser })
        {
            var called = false;
            RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
            var accessor = new TestCurrentUserAccessor(user);
            var member = new MemberWriteGuardMiddleware(next);
            await new DemoReadOnlyMiddleware(ctx => member.InvokeAsync(ctx, accessor)).InvokeAsync(context, accessor);
            Assert.Equal(expected, called);
        }
    }

    private static SongMetadata Song(int id, string parent, int? track = 1, int? disc = 1, Guid? owner = null) => new()
    {
        Id = id, OwnerUserId = owner ?? TestUsers.OwnerId, SourcePath = $"{parent}/{id:000}.mp3", FileName = $"{id:000}.mp3",
        Extension = ".mp3", FileSizeBytes = 100, LastModifiedUtc = Stamp, IndexedAtUtc = Stamp,
        Artist = "Example Ensemble", AlbumArtist = "Example Ensemble", Album = "Example Program", Title = $"Movement {id}",
        TrackNumber = track, DiscNumber = disc, DurationMs = 180000, EnrichmentStatus = EnrichmentStatus.Matched
    };
    private static SongProviderAttempt Attempt(int id, int song, string title, int duration) => new()
    {
        Id = id, SongId = song, Provider = id == 1 ? EnrichmentProvider.MusicBrainzWeb : EnrichmentProvider.SpotifyAPI,
        Status = ProviderAttemptStatus.Matched, AttemptedAtUtc = Stamp,
        MatchedDataJson = $$"""{"Title":"{{title}}","Album":"Example Program","DurationSeconds":{{duration}},"MatchConfidence":0.9,"MatchWarnings":["duration_mismatch"],"Fingerprint":"PROVIDER_CANARY","PlainLyrics":"LYRICS_CANARY","Secret":"TOKEN_CANARY"}"""
    };
    private static SongDuplicateLink Link(int id, int low, int high, DuplicateLinkStatus status = DuplicateLinkStatus.Active) => new()
    {
        Id = id, OwnerUserId = TestUsers.OwnerId, SongIdLow = low, SongIdHigh = high, Status = status,
        Confidence = DuplicateConfidence.Confirmed, Reasons = DuplicateMatchReason.ExactFingerprint, DetectedAtUtc = Stamp
    };
    private static SongMetadataChange Change(int id, int song, string field, string? value, DateTime stamp) => new()
    { Id = id, SongId = song, FieldName = field, NewValue = value, Source = "ManualReview", CreatedAtUtc = stamp, AppliedAtUtc = stamp };
    private static async Task<AlbumReviewContext> Read(MusicHoarderDbContext db, AlbumReviewContextRequest request) =>
        Value<AlbumReviewContext>(await AlbumReviewEndpoints.ReadContext(request, db, Owner, new FileSystem(), default));
    private static async Task<AlbumReviewGroupsPage> Search(MusicHoarderDbContext db, int offset = 0, int limit = 25, string? query = null) =>
        Value<AlbumReviewGroupsPage>(await AlbumReviewEndpoints.Search(db, Owner, default, query: query, offset: offset, limit: limit));
    private static T Value<T>(IResult result) => Assert.IsType<T>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    private static int? Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
    private static async Task<string> Snapshot(MusicHoarderDbContext db) => JsonSerializer.Serialize(new
    {
        songs = await db.Songs.AsNoTracking().OrderBy(s => s.Id).ToArrayAsync(),
        attempts = await db.SongProviderAttempts.AsNoTracking().OrderBy(a => a.Id).Select(a => new { a.Id, a.SongId, a.MatchedDataJson, a.AttemptedAtUtc }).ToArrayAsync(),
        changes = await db.SongMetadataChanges.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.NewValue, c.AppliedAtUtc }).ToArrayAsync()
    });

    internal sealed class TestDatabase : IAsyncDisposable
    {
        public required MusicHoarderDbContext Db { get; init; }
        public required string ConnectionString { get; init; }
        public required string AdminConnection { get; init; }
        public required string Name { get; init; }
        public static async Task<TestDatabase> CreateAsync(bool migrate = false)
        {
            var config = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MH_REVIEW_TEST_POSTGRES"));
            if (config.Database != "mh_review_test" || !(config.Host == "localhost" || config.Host == "127.0.0.1" || config.Host!.StartsWith('/')))
                throw new InvalidOperationException("Use an explicitly disposable local server and Database=mh_review_test.");
            config.Database = "postgres";
            var admin = config.ConnectionString;
            var name = "mh_review_test_" + Guid.NewGuid().ToString("N");
            await using (var conn = new NpgsqlConnection(admin))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            config.Database = name;
            config.Pooling = false;
            var db = new MusicHoarderDbContext(new DbContextOptionsBuilder()
                .UseNpgsql(config.ConnectionString, pg => pg.EnableRetryOnFailure()).Options);
            try { if (migrate) await db.Database.MigrateAsync(); else await db.Database.EnsureCreatedAsync(); }
            catch { await db.DisposeAsync(); throw; }
            return new() { Db = db, ConnectionString = config.ConnectionString, AdminConnection = admin, Name = name };
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await using var conn = new NpgsqlConnection(AdminConnection);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE {Name} WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private sealed class RetrySnapshotProbe : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool InjectedTransientFailure { get; private set; }
        public int ReadOnlySetCount { get; private set; }
        public List<(IsolationLevel Isolation, string ReadOnly)> Snapshots { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText == "SET TRANSACTION READ ONLY") ReadOnlySetCount++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Assert.NotNull(command.Transaction);
                using var check = command.Connection!.CreateCommand();
                check.Transaction = command.Transaction;
                check.CommandText = "SELECT current_setting('transaction_read_only')";
                var readOnly = (string)(await check.ExecuteScalarAsync(cancellationToken))!;
                Snapshots.Add((command.Transaction.IsolationLevel, readOnly));
                if (!InjectedTransientFailure)
                {
                    InjectedTransientFailure = true;
                    throw new NpgsqlException("Synthetic transient read failure", new TimeoutException("Synthetic timeout"));
                }
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MH_REVIEW_TEST_POSTGRES")))
            Skip = "Set MH_REVIEW_TEST_POSTGRES for disposable local PostgreSQL integration tests.";
    }
}

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MH_REVIEW_TEST_POSTGRES")))
            Skip = "Set MH_REVIEW_TEST_POSTGRES for disposable local PostgreSQL integration tests.";
    }
}
