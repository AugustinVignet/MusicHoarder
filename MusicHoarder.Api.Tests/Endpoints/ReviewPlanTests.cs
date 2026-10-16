using System.IO.Abstractions;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Tests.Auth;
using Store = MusicHoarder.Api.Tests.Endpoints.AlbumReviewContextTests.TestDatabase;

namespace MusicHoarder.Api.Tests.Endpoints;

public sealed class ReviewPlanTests
{
    private static readonly Guid Owner = TestUsers.OwnerId;
    private static readonly TestCurrentUserAccessor User = new(TestCurrentUserAccessor.OwnerUser);
    private static readonly IFileSystem Files = new FileSystem();
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static IDestinationPathResolver Resolver(string root = "/destination/review") => new DestinationPathResolver(
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { DestinationDirectory = root }));

    [PostgresFact]
    public async Task ThirteenMatchedAndOneNeedsReview_ExactSubset_AuditAndCopiesPreserved_AppliedOnce()
    {
        await using var store = await Store.CreateAsync();
        var db = store.Db;
        db.Songs.AddRange(Enumerable.Range(1, 14).Select(Song));
        await db.SaveChangesAsync();
        var before = await db.Songs.AsNoTracking().Where(s => s.Id != 1 && s.Id != 14).OrderBy(s => s.Id).ToArrayAsync();
        var request = await Request(db, [Patch(1, new { TrackNumber = 15, DiscNumber = 2, TotalDiscs = 2 }), Patch(14, new { }, true)]);
        var preview = await ReviewPlanService.PreviewAsync(db, Files, Resolver(), Owner, request, default);
        Assert.True(preview.CanApply);
        Assert.Equal(14, preview.ContextSongIds.Count);
        Assert.Equal(2, preview.Tracks.Count);
        Assert.Equal("Matched", preview.Tracks[0].BeforeStatus);
        Assert.Contains("2-15", preview.Tracks[0].PlannedPath);
        Assert.Equal(0, await db.SongMetadataChanges.CountAsync());
        Assert.Equal(EnrichmentStatus.NeedsReview, (await db.Songs.AsNoTracking().SingleAsync(s => s.Id == 14)).EnrichmentStatus);
        var result = await ReviewPlanService.ApplyAsync(db, Files, Resolver(), Owner, preview.Id, preview.Revision, default);
        Assert.Equal("metadataApplied", result.State);
        Assert.Equal("awaitingVerifiedBuild", result.CopyState);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await ReviewPlanService.ApplyAsync(db, Files, Resolver(), Owner, preview.Id, preview.Revision, default)));
        db.ChangeTracker.Clear();
        var edited = await db.Songs.Where(s => s.Id == 1 || s.Id == 14).OrderBy(s => s.Id).ToArrayAsync();
        Assert.All(edited, s =>
        {
            Assert.Equal(EnrichmentStatus.Matched, s.EnrichmentStatus);
            Assert.Equal(preview.Id, s.ReviewPlanId);
            Assert.Equal(LibraryBuildStatus.AwaitingReviewBuild, s.LibraryBuildStatus);
            Assert.Equal($"/old/{s.Id}.mp3", s.DestinationPath);
            Assert.Equal($"/older/{s.Id}.mp3", s.PreviousDestinationPath);
            Assert.Equal(Stamp, s.LibraryBuiltAtUtc);
        });
        Assert.Equal(2, edited[0].DiscNumber);
        Assert.Equal(15, edited[0].TrackNumber);
        Assert.Equal("Example Ensemble", edited[0].Artist);
        var after = await db.Songs.AsNoTracking().Where(s => s.Id != 1 && s.Id != 14).OrderBy(s => s.Id).ToArrayAsync();
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        var audits = await db.SongMetadataChanges.ToArrayAsync();
        Assert.Equal(3, audits.Length);
        Assert.All(audits, c =>
        {
            Assert.Equal(Owner, c.ActorUserId); Assert.Equal(request.Reason, c.Reason);
            Assert.Equal(preview.Revision, c.ReviewPlanRevision); Assert.Contains("fixture:booklet", c.EvidenceReferencesJson);
            Assert.Equal("ManualReview", c.Source); Assert.NotNull(c.AppliedAtUtc);
        });
        var current = await Context(db);
        Assert.Equal(preview.Id, current.Tracks[0].ReviewPlanId);
        Assert.All(current.Tracks[0].Changes, c => Assert.Equal(Owner, c.ActorUserId));
        Assert.Empty(await LibraryBuildQuery.BuildCandidates(db.Songs.Where(s => s.Id == 1 || s.Id == 14), null, 3).ToArrayAsync());
    }

    [PostgresFact]
    public async Task NullIsDurableUnknown_OmittedFieldsAndPriorManualValuesSurviveReloadAndAutomaticCorrection()
    {
        await using var store = await Store.CreateAsync();
        var db = store.Db;
        var song = Song(1);
        song.Year = 2000; song.ReleaseDate = "2000-03-15"; song.OriginalReleaseDate = "1990";
        song.MusicBrainzReleaseId = "11111111-1111-4111-8111-111111111111";
        song.IsCompilation = true;
        db.Songs.Add(song); await db.SaveChangesAsync();
        var first = await Preview(db, [Patch(1, new { Title = "Reviewed title" })]);
        await Apply(db, first);
        var fields = new Dictionary<string, object?> { ["Year"] = null, ["ReleaseDate"] = null, ["OriginalReleaseDate"] = null,
            ["MusicBrainzReleaseId"] = null, ["IsCompilation"] = null };
        var next = await Preview(db, [Patch(1, fields)]);
        Assert.Contains(next.Tracks.Single().Fields, f => f.Field == "Year" && f.Intent == "explicitUnknown" && (int)f.Before! == 2000);
        await Apply(db, next);
        db.ChangeTracker.Clear(); song = await db.Songs.SingleAsync();
        await ManualReviewMetadata.LoadAsync(db, [song]);
        Assert.True(ManualReviewMetadata.IsExplicitUnknown(song, "Year"));
        Assert.True(ManualReviewMetadata.IsExplicitUnknown(song, "IsCompilation"));
        Assert.Equal("Reviewed title", song.Title);
        Assert.True(song.HasManualReviewValue("Title"));
        var elected = AlbumIdentity.FromSong(song) with { Year = 1990, IsCompilation = true, MusicBrainzReleaseId = Guid.NewGuid().ToString() };
        Assert.Empty(song.ApplyIdentityCorrection(elected));
        Assert.Empty(song.ApplyCanonicalCorrection("Another Album", "Another Artist", 1990, 99, 1));
        Assert.Null(AlbumIdentity.FromSong(song).WithManualReview(song).Year);
        Assert.Equal("Reviewed title", song.Title); Assert.Null(song.Year); Assert.Null(song.MusicBrainzReleaseId);
        await db.SaveChangesAsync();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "example ensemble", AlbumKey = "example program", DisplayArtist = "Example Ensemble", DisplayTitle = "Example Program",
            Year = 1990, Status = CanonicalAlbumStatus.Fetched,
            Tracks = [new CanonicalAlbumTrack { TrackNumber = 99, DiscNumber = 1, Title = "Reviewed title" }]
        });
        await db.SaveChangesAsync();
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions());
        var healer = new AlbumSplitHealer(db, new AlbumIdentityReconciler(), Resolver(), options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AlbumSplitHealer>.Instance);
        Assert.Equal(new AlbumSplitHealResult(0, 0, 0), await healer.HealAsync());
        var consolidator = new CanonicalAlbumConsolidator(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<CanonicalAlbumConsolidator>.Instance);
        await consolidator.ConsolidateAsync(db, "Example Ensemble", "Example Program", default);
        await db.SaveChangesAsync();
        var context = await Context(db);
        Assert.Null(context.Tracks.Single().Current.Values["IsCompilation"]);
        Assert.Contains(context.Tracks.Single().ManualFields, f => f.Field == "Year" && f.State == "explicitUnknown");
    }

    [PostgresTheory]
    [InlineData("target")]
    [InlineData("neighbor")]
    [InlineData("membership")]
    [InlineData("provider")]
    [InlineData("copy")]
    public async Task AnyRelevantContextDrift_RejectsWithDiffAndNoPartialChanges(string kind)
    {
        await using var store = await Store.CreateAsync(); var db = store.Db;
        db.Songs.AddRange(Song(1), Song(2)); await db.SaveChangesAsync();
        var plan = await Preview(db, [Patch(1, new { Title = "Approved title" })]);
        db.ChangeTracker.Clear();
        switch (kind)
        {
            case "target": (await db.Songs.SingleAsync(s => s.Id == 1)).Title = "Concurrent title"; break;
            case "neighbor": (await db.Songs.SingleAsync(s => s.Id == 2)).Year = 2001; break;
            case "membership": db.Songs.Add(Song(3)); break;
            case "provider": db.SongProviderAttempts.Add(new SongProviderAttempt { SongId = 2, Provider = EnrichmentProvider.SpotifyAPI,
                Status = ProviderAttemptStatus.NoMatch, AttemptedAtUtc = Stamp }); break;
            case "copy": (await db.Songs.SingleAsync(s => s.Id == 2)).LibraryBuiltAtUtc = Stamp.AddSeconds(1); break;
        }
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ReviewPlanException>(() => Apply(db, plan));
        Assert.Equal("plan_stale", error.Error.Code); Assert.NotEmpty(error.Error.Differences!);
        Assert.Empty(await db.SongMetadataChanges.ToArrayAsync());
        Assert.Null((await db.Songs.AsNoTracking().SingleAsync(s => s.Id == 1)).ReviewPlanId);
    }

    [PostgresFact]
    public async Task TwoConcurrentPlans_OneWinner_AndStaleEfWriterCannotOverwriteTheApprovedRow()
    {
        await using var store = await Store.CreateAsync(); var db = store.Db;
        db.Songs.Add(Song(1)); await db.SaveChangesAsync();
        var first = await Preview(db, [Patch(1, new { Title = "First decision" })]);
        var second = await Preview(db, [Patch(1, new { Title = "Second decision" })]);
        await using var staleDb = new MusicHoarderDbContext(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString).Options);
        var stale = await staleDb.Songs.SingleAsync();
        await using var otherDb = new MusicHoarderDbContext(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString, p => p.EnableRetryOnFailure()).Options);
        async Task<bool> TryApply(MusicHoarderDbContext connection, ReviewPlanPreview p)
        {
            try { await Apply(connection, p); return true; }
            catch (ReviewPlanException e) { Assert.Contains(e.Error.Code, new[] { "plan_stale", "writer_busy" }); return false; }
        }
        var results = await Task.WhenAll(TryApply(db, first), TryApply(otherDb, second));
        Assert.Single(results, r => r);
        stale.Title = "Late automatic write";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        Assert.Single(await db.ReviewPlans.AsNoTracking().Where(p => p.AppliedAtUtc != null).ToArrayAsync());
        Assert.Single(await db.SongMetadataChanges.AsNoTracking().ToArrayAsync());
    }

    [PostgresFact]
    public async Task PathPolicyAndFileObservationsAreBound_AndOccupiedPathsBlockApplication()
    {
        await using var store = await Store.CreateAsync(); var db = store.Db;
        var root = Path.Combine(Path.GetTempPath(), "mh-review-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            db.Songs.Add(Song(1)); await db.SaveChangesAsync();
            var request = await Request(db, [Patch(1, new { Title = "New title" })]);
            var plan = await ReviewPlanService.PreviewAsync(db, Files, Resolver(root), Owner, request, default);
            var changed = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.ApplyAsync(db, Files, Resolver(root + "-changed"), Owner, plan.Id, plan.Revision, default));
            Assert.Equal("plan_stale", changed.Error.Code);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Tracks.Single().PlannedPath)!);
            await File.WriteAllTextAsync(plan.Tracks.Single().PlannedPath, "synthetic occupant");
            await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.ApplyAsync(db, Files, Resolver(root), Owner, plan.Id, plan.Revision, default));
            var blocked = await ReviewPlanService.PreviewAsync(db, Files, Resolver(root), Owner, request, default);
            Assert.False(blocked.CanApply);
            Assert.Contains(blocked.Conflicts, c => c.Kind == "destinationOccupiedOrUnknown");
            var error = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.ApplyAsync(db, Files, Resolver(root), Owner, blocked.Id, blocked.Revision, default));
            Assert.Equal("plan_conflicts", error.Error.Code);
            Assert.Equal("synthetic occupant", await File.ReadAllTextAsync(plan.Tracks.Single().PlannedPath));
            Assert.Empty(await db.SongMetadataChanges.ToArrayAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [PostgresFact]
    public async Task PossiblePositionConflictPreservesUnknownDisc_AndExplicitFixResolvesIt()
    {
        await using var store = await Store.CreateAsync(); var db = store.Db;
        var a = Song(1); a.DiscNumber = null;
        var b = Song(2); b.TrackNumber = 1;
        db.Songs.AddRange(a, b); await db.SaveChangesAsync();
        var blocked = await Preview(db, [Patch(1, new { Title = "Distinct take" })]);
        Assert.False(blocked.CanApply); Assert.Contains(blocked.Conflicts, c => c.Kind == "possibleAlbumPosition");
        Assert.Null((await db.Songs.AsNoTracking().SingleAsync(s => s.Id == 1)).DiscNumber);
        var fixedPlan = await Preview(db, [Patch(1, new { TrackNumber = 3 })]);
        Assert.True(fixedPlan.CanApply); await Apply(db, fixedPlan);
        Assert.Null((await db.Songs.AsNoTracking().SingleAsync(s => s.Id == 1)).DiscNumber);
    }

    [PostgresTheory]
    [InlineData("{\"Year\":null}")]
    [InlineData("{\"ReleaseDate\":\"2026-02-30\"}")]
    [InlineData("{\"DiscNumber\":3,\"TotalDiscs\":2}")]
    [InlineData("{\"MusicBrainzReleaseId\":\"not-a-uuid\"}")]
    [InlineData("{\"ArtistMusicBrainzIds\":\"11111111-1111-4111-8111-111111111111\"}")]
    [InlineData("{\"Year\":\"2000\"}")]
    [InlineData("{\"Artists\":\"Artist One;;Artist Two\"}")]
    [InlineData("{\"ReleaseTypePrimary\":\"album\",\"ReleaseTypes\":\"ep\"}")]
    [InlineData("{\"Title\":\"\"}")]
    [InlineData("{\"OwnerUserId\":null}")]
    public async Task InvalidTypesAndIncoherentTouchedFieldsAreRejected(string fields)
    {
        await using var store = await Store.CreateAsync(); var db = store.Db; var song = Song(1);
        song.Year = 2000; song.ReleaseDate = "2000-03-15"; db.Songs.Add(song); await db.SaveChangesAsync();
        var patch = new ReviewPlanTrackRequest(1, false, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fields)!);
        var request = await Request(db, [patch]);
        var e = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.PreviewAsync(db, Files, Resolver(), Owner, request, default));
        Assert.Equal(400, e.StatusCode); Assert.Empty(await db.ReviewPlans.ToArrayAsync());
    }

    [PostgresFact]
    public async Task NameCorrectionDoesNotChooseEdition_AndNewGroupRequiresReviewedNeighbors()
    {
        await using var store = await Store.CreateAsync(); var db = store.Db;
        var a = Song(1); a.MusicBrainzReleaseId = "historical-nonstandard-id";
        var b = Song(2); b.Album = "Other program"; b.SourcePath = "/source/other/002.mp3"; b.DestinationPath = "/other-built/2.mp3";
        db.Songs.AddRange(a, b); await db.SaveChangesAsync();
        var named = await Preview(db, [Patch(1, new { Artist = "Corrected performer" })]);
        await Apply(db, named);
        Assert.Equal(a.MusicBrainzReleaseId, (await db.Songs.AsNoTracking().SingleAsync(s => s.Id == 1)).MusicBrainzReleaseId);
        var request = await Request(db, [Patch(1, new { Album = "Other program" })]);
        var error = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.PreviewAsync(db, Files, Resolver(), Owner, request, default));
        Assert.Equal("expand_context", error.Error.Code);
        var expanded = await Context(db, new(["/source/album", "/source/other"]));
        var preview = await ReviewPlanService.PreviewAsync(db, Files, Resolver(), Owner,
            request with { Scope = new(["/source/album", "/source/other"]), ContextRevision = expanded.Revision }, default);
        Assert.Contains(2, preview.ContextSongIds);
    }

    [PostgresFact]
    public async Task OwnerSelectionDeletionAndAuthorizationAreEnforced()
    {
        await using var store = await Store.CreateAsync(); var db = store.Db;
        db.Songs.Add(Song(1)); await db.SaveChangesAsync();
        var preview = await Preview(db, [Patch(1, new { Title = "New" })]);
        var notFound = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.GetAsync(db, TestUsers.FriendId, preview.Id, default));
        Assert.Equal(404, notFound.StatusCode);
        var outside = await Request(db, [Patch(2, new { Title = "New" })]);
        Assert.Equal("outside_selection", (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.PreviewAsync(db, Files, Resolver(), Owner, outside, default))).Error.Code);
        var forbidden = await AlbumReviewEndpoints.ApplyPlan(preview.Id, new(preview.Revision), db,
            new TestCurrentUserAccessor(TestCurrentUserAccessor.FriendUser), Files, Resolver(), default);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>(forbidden);
        (await db.Songs.SingleAsync()).DeletedAtUtc = Stamp; await db.SaveChangesAsync();
        Assert.Equal("plan_stale", (await Assert.ThrowsAsync<ReviewPlanException>(() => Apply(db, preview))).Error.Code);
        Assert.Empty(await db.SongMetadataChanges.ToArrayAsync());
    }

    [PostgresFact]
    public async Task RetryingExecutionStrategy_ReplaysFreshPreviewAndApplyTransactions_WithoutDuplicateAudit()
    {
        await using var store = await Store.CreateAsync(); store.Db.Songs.Add(Song(1)); await store.Db.SaveChangesAsync();
        var request = await Request(store.Db, [Patch(1, new { Title = "Retry-safe decision" })]);
        var probe = new TransientProbe { Mode = "preview" };
        await using var db = new MusicHoarderDbContext(new DbContextOptionsBuilder()
            .UseNpgsql(store.ConnectionString, pg => pg.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(5), null))
            .AddInterceptors(probe).Options);
        var preview = await ReviewPlanService.PreviewAsync(db, Files, Resolver(), Owner, request, default);
        Assert.Equal(1, probe.Failures); Assert.Null(db.Database.CurrentTransaction);
        probe.Mode = "apply";
        await Apply(db, preview);
        Assert.Equal(2, probe.Failures); Assert.Null(db.Database.CurrentTransaction);
        Assert.Single(await db.ReviewPlans.AsNoTracking().ToArrayAsync());
        Assert.Single(await db.SongMetadataChanges.AsNoTracking().ToArrayAsync());
        Assert.Equal("Retry-safe decision", (await db.Songs.AsNoTracking().SingleAsync()).Title);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReviewPlanService.ApplyAsync(db, Files, Resolver(), Owner,
            preview.Id, preview.Revision, cancel.Token));
    }

    private sealed class TransientProbe : DbCommandInterceptor
    {
        public string? Mode { get; set; }
        public int Failures { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if ((Mode == "preview" && command.CommandText.Contains("FROM \"ReviewPlans\""))
                || (Mode == "apply" && command.CommandText.Contains("UPDATE \"Songs\"")))
            {
                Assert.NotNull(command.Transaction); Mode = null; Failures++;
                throw new NpgsqlException("Synthetic transient plan failure", new TimeoutException("Synthetic timeout"));
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [PostgresFact]
    public async Task ProductionHttpComposition_PreviewGetApply_AndTypedOpenApi()
    {
        await using var store = await Store.CreateAsync(); store.Db.Songs.Add(Song(1)); await store.Db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddDbContext<MusicHoarderDbContext>(o => o.UseNpgsql(store.ConnectionString));
        builder.EnrichNpgsqlDbContext<MusicHoarderDbContext>();
        builder.Services.AddSingleton<ICurrentUserAccessor>(User);
        builder.Services.AddSingleton(Files); builder.Services.AddSingleton(Resolver()); builder.Services.AddOpenApi();
        builder.Services.AddSingleton<MusicHoarder.Api.Review.Copy.IReviewAudioCommandRunner>(
            new MusicHoarder.Api.Review.Copy.ReviewAudioCommandRunner("ffmpeg"));
        await using var app = builder.Build(); app.MapAlbumReviewEndpoints(); app.MapOpenApi(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var context = (await (await client.PostAsJsonAsync("/api/review/album-context", new AlbumReviewContextRequest(["/source/album"]))).Content.ReadFromJsonAsync<AlbumReviewContext>())!;
        var request = new ReviewPlanRequest(new(["/source/album"]), context.Revision, "Correct booklet credit", ["fixture:booklet"], [Patch(1, new { Title = "Reviewed via HTTP" })]);
        using var response = await client.PostAsJsonAsync("/api/review/plans/preview", request); response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<ReviewPlanPreview>())!;
        var fetched = await client.GetFromJsonAsync<ReviewPlanPreview>($"/api/review/plans/{plan.Id}"); Assert.Equal(plan.Revision, fetched!.Revision);
        using var applied = await client.PostAsJsonAsync($"/api/review/plans/{plan.Id}/apply", new ReviewPlanApplyRequest(plan.Revision)); applied.EnsureSuccessStatusCode();
        var receipt = (await applied.Content.ReadFromJsonAsync<ReviewPlanResult>())!;
        Assert.Equal("metadataApplied", receipt.State); Assert.NotNull(receipt.JobId);
        var job = (await client.GetFromJsonAsync<ReviewCopyJobResult>($"/api/review/jobs/{receipt.JobId}"))!;
        Assert.True(job.MetadataApplied); Assert.False(job.CopiesVerified); Assert.Equal("pending", job.State);
        using var retry = await client.PostAsync($"/api/review/jobs/{receipt.JobId}/retry", null);
        retry.EnsureSuccessStatusCode();
        using var stale = await client.PostAsJsonAsync($"/api/review/plans/{plan.Id}/apply", new ReviewPlanApplyRequest(new string('0', 64)));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
        var api = await client.GetStringAsync("/openapi/v1.json"); Assert.Contains("ReviewPlanRequest", api); Assert.Contains("ReviewPlanPreview", api);
        await app.StopAsync();
    }

    [PostgresFact]
    public async Task EveryAllowedFieldSupportsValueThenExplicitNull_WithoutInferringAnEdition()
    {
        await using var store = await Store.CreateAsync(); var db = store.Db;
        db.Songs.Add(Song(1)); await db.SaveChangesAsync();
        var values = ReviewFields.Names.ToDictionary(n => n, n => (object?)"Example value");
        foreach (var n in new[] { "Year", "TrackNumber", "DiscNumber", "TotalDiscs", "TotalTracks" }) values[n] = 1;
        values["Year"] = 2000; values["ReleaseDate"] = "2000-03-15"; values["OriginalReleaseDate"] = "1990";
        values["IsCompilation"] = true;
        foreach (var n in new[] { "MusicBrainzId", "MusicBrainzReleaseId", "MusicBrainzReleaseGroupId", "ArtistMusicBrainzIds", "AlbumArtistMusicBrainzId" })
            values[n] = "11111111-1111-4111-8111-111111111111";
        var plan = await Preview(db, [Patch(1, values)]); Assert.True(plan.CanApply); await Apply(db, plan);
        var actual = (await Context(db)).Tracks.Single().Current.Values;
        foreach (var (name, value) in values) Assert.Equal(JsonSerializer.Serialize(value), JsonSerializer.Serialize(actual[name]));
        var unknown = await Preview(db, [Patch(1, values.ToDictionary(p => p.Key, _ => (object?)null))]);
        await Apply(db, unknown);
        var context = await Context(db);
        Assert.All(context.Tracks.Single().Current.Values, p => Assert.Null(p.Value));
        Assert.Equal(ReviewFields.Names.Length, context.Tracks.Single().ManualFields.Count);
        Assert.All(context.Tracks.Single().ManualFields, f => Assert.Equal("explicitUnknown", f.State));
    }

    [PostgresFact]
    public async Task AdditiveMigration_UpAndDown_PreservesSongsAndPreviousSchema()
    {
        await using var store = await Store.CreateAsync(migrate: true); var db = store.Db;
        Assert.False(db.Database.HasPendingModelChanges());
        db.Songs.Add(Song(1)); await db.SaveChangesAsync();
        var plan = await Preview(db, [Patch(1, new { Title = "Reviewed" })]); await Apply(db, plan);
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Contains(applied, m => m.EndsWith("AddExactReviewPlans"));
        Assert.EndsWith("AddReviewRequestAliases", applied[^1]);
        await db.GetService<IMigrator>().MigrateAsync(applied[^4]);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"Songs\"").SingleAsync());
        await db.GetService<IMigrator>().MigrateAsync();
        Assert.Empty(await db.ReviewPlans.ToArrayAsync());
        Assert.Equal("Reviewed", (await db.Songs.AsNoTracking().SingleAsync()).Title);
    }

    private static SongMetadata Song(int id) => new()
    {
        Id = id, OwnerUserId = Owner, SourcePath = $"/source/album/{id:000}.mp3", FileName = $"{id:000}.mp3", Extension = ".mp3",
        FileSizeBytes = 100, LastModifiedUtc = Stamp, IndexedAtUtc = Stamp, Artist = "Example Ensemble", AlbumArtist = "Example Ensemble",
        Album = "Example Program", Title = $"Movement {id}", TrackNumber = id, DiscNumber = 1,
        EnrichmentStatus = id == 14 ? EnrichmentStatus.NeedsReview : EnrichmentStatus.Matched,
        DestinationPath = $"/old/{id}.mp3", PreviousDestinationPath = $"/older/{id}.mp3", LibraryBuildStatus = LibraryBuildStatus.Done, LibraryBuiltAtUtc = Stamp
    };
    private static ReviewPlanTrackRequest Patch(int id, object fields, bool approve = false) =>
        new(id, approve, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!);
    private static Task<AlbumReviewContext> Context(MusicHoarderDbContext db, AlbumReviewContextRequest? scope = null) =>
        AlbumReviewContextReader.ReadAsync(db, Files, Owner, scope ?? new(["/source/album"]), default);
    private static async Task<ReviewPlanRequest> Request(MusicHoarderDbContext db, ReviewPlanTrackRequest[] patches) =>
        new(new(["/source/album"]), (await Context(db)).Revision, "Verified against synthetic booklet", ["fixture:booklet"], patches);
    private static async Task<ReviewPlanPreview> Preview(MusicHoarderDbContext db, ReviewPlanTrackRequest[] patches) =>
        await ReviewPlanService.PreviewAsync(db, Files, Resolver(), Owner, await Request(db, patches), default);
    private static Task<ReviewPlanResult> Apply(MusicHoarderDbContext db, ReviewPlanPreview plan) =>
        ReviewPlanService.ApplyAsync(db, Files, Resolver(), Owner, plan.Id, plan.Revision, default);
}
