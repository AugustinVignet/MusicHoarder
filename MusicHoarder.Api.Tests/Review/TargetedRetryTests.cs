using System.IO.Abstractions;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Review.Retry;
using MusicHoarder.Api.Scanner;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Endpoints;
using MusicHoarder.Api.Tests.Library;
using Store = MusicHoarder.Api.Tests.Endpoints.AlbumReviewContextTests.TestDatabase;

namespace MusicHoarder.Api.Tests.Review;

public sealed class TargetedRetryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mh-targeted-" + Guid.NewGuid().ToString("N"));
    private readonly IFileSystem files = new FileSystem();
    private static readonly Guid Owner = TestUsers.OwnerId;
    private IReviewAudioCommandRunner Tools => new ReviewAudioCommandRunner(AudioTools.Ffmpeg!);
    private IDestinationPathResolver Resolver => new DestinationPathResolver(Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { DestinationDirectory = Path.Combine(root, "destination") }));
    private static FpcalcOutcome Good => FpcalcOutcome.Success(new(12, FpcalcValidationTests.Fingerprint));
    public TargetedRetryTests() => Directory.CreateDirectory(root);
    private TargetedRetryProcessor Processor(MusicHoarderDbContext db, IFpcalcService fp, FakeEnrichment? provider = null, ITargetedRetryCheckpoints? points = null)
        => new(db, files, fp, Tools, provider ?? new(), new(db, files, new TagLibLibraryTagWriter(), Resolver, Tools), points);
    private TargetedRetryRequest Request(int[] ids, string[]? stages = null, bool providers = false, Guid? copy = null,
        bool recompute = false, bool respect = true) => new(Guid.NewGuid(), ids, stages ?? ["fingerprint"], providers, copy, recompute, respect);

    [PostgresFact]
    public async Task TwoFailedFingerprints_RecoverAfterNewContext_NoOtherRowsProvidersCopiesOrDuplicateFlagsChange()
    {
        await using var store = await Store.CreateAsync(); var songs = await AddSongs(store.Db, 5);
        var others = JsonSerializer.Serialize(await store.Db.Songs.AsNoTracking().Where(s => s.Id != songs[0].Id && s.Id != songs[1].Id).OrderBy(s => s.Id).ToArrayAsync());
        var fp = new FakeFp(_ => FpcalcOutcome.Failure("synthetic_fpcalc_failure")); var provider = new FakeEnrichment();
        var first = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([songs[0].Id, songs[1].Id]), default);
        Assert.True(await Processor(store.Db, fp, provider).RunNextAsync(default)); Assert.Equal(2, fp.Calls); Assert.Equal(0, provider.Calls);
        await using var resumed = NewDb(store);
        var failures = await TargetedRetryService.FailuresAsync(resumed, Owner, "fingerprint", 0, 1, default);
        Assert.Single(failures.Items); Assert.True(failures.HasMore);
        Assert.Equal("needsAttention", (await TargetedRetryService.GetAsync(resumed, Owner, first.Id, default)).State);
        Assert.False(await Processor(resumed, fp, provider).RunNextAsync(default)); // no hot retry / no RAM failure cache
        var retry = await TargetedRetryService.CreateAsync(resumed, files, Owner, Request([songs[0].Id, songs[1].Id]), default);
        var valid = new FakeFp(_ => Good);
        await Processor(resumed, valid, provider).RunNextAsync(default);
        Assert.Equal("completed", (await TargetedRetryService.GetAsync(resumed, Owner, retry.Id, default)).State);
        Assert.Equal(2, valid.Calls); Assert.Equal(0, provider.Calls);
        var after = await resumed.Songs.AsNoTracking().OrderBy(s => s.Id).ToArrayAsync();
        Assert.All(after.Take(2), s => { Assert.Equal(FpcalcValidationTests.Fingerprint, s.Fingerprint); Assert.Equal(12, s.DurationSeconds);
            Assert.True(s.AutomationHeld); Assert.False(s.IsReadyForEnrichment); Assert.False(s.IsReadyForBuild); Assert.False(s.IsDuplicate); });
        Assert.Equal(others, JsonSerializer.Serialize(after.Skip(2).ToArray()));
        Assert.Empty(await resumed.SongDuplicateLinks.ToArrayAsync()); Assert.Empty(await resumed.ReviewCopyJobs.ToArrayAsync());
        Assert.Empty(await resumed.SongProviderAttempts.ToArrayAsync());
        Assert.All(await resumed.PipelineStageStates.ToArrayAsync(), s => Assert.Equal(2, s.Attempts));
        Assert.Equal(4, await resumed.PipelineStageAttempts.CountAsync());
    }

    [PostgresFact]
    public async Task SameRequestAndContent_ReturnsSameJob_DifferentContentIsRefused()
    {
        await using var store = await Store.CreateAsync(); var songs = await AddSongs(store.Db, 2);
        var request = Request(songs.Select(s => s.Id).ToArray());
        var first = await TargetedRetryService.CreateAsync(store.Db, files, Owner, request, default);
        var again = await TargetedRetryService.CreateAsync(store.Db, files, Owner, request with { SongIds = request.SongIds.Reverse().ToArray() }, default);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(again));
        var changed = await Assert.ThrowsAsync<ReviewPlanException>(() => TargetedRetryService.CreateAsync(store.Db, files, Owner,
            request with { RecomputeSuccessful = true }, default));
        Assert.Equal("request_key_reused", changed.Error.Code); Assert.Single(await store.Db.TargetedRetryJobs.ToArrayAsync());
        var fp = new FakeFp(_ => Good); await Processor(store.Db, fp).RunNextAsync(default);
        var replay = await TargetedRetryService.CreateAsync(store.Db, files, Owner, request, default);
        Assert.Equal(first.Id, replay.Id); Assert.Equal("completed", replay.State); Assert.Equal(2, fp.Calls);
        Assert.False(await Processor(store.Db, fp).RunNextAsync(default));
    }

    [PostgresTheory]
    [InlineData("started:fingerprint", 1)]
    [InlineData("recorded:fingerprint", 1)]
    public async Task InterruptedJob_RestartsWithoutReplayingAcknowledgedFingerprint(string boundary, int expectedCalls)
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        var job = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id]), default);
        var points = new Points(boundary); var fp = new FakeFp(_ => Good);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Processor(store.Db, fp, points: points).RunNextAsync(default));
        await using var resumed = NewDb(store); await Processor(resumed, fp).RunNextAsync(default);
        Assert.Equal("completed", (await TargetedRetryService.GetAsync(resumed, Owner, job.Id, default)).State);
        Assert.Equal(expectedCalls, fp.Calls); Assert.Single(await resumed.PipelineStageAttempts.ToArrayAsync());
    }

    [PostgresTheory]
    [InlineData(3, "")]
    [InlineData(0, "audio decoder diagnostic")]
    public async Task NonzeroOrDiagnosticResult_StaysQualifiedAndNeverFeedsTheNextStage(int exitCode, string errors)
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        var fp = new FakeFp(_ => FpcalcService.Validate(new(exitCode, FpcalcValidationTests.ValidJson, errors)));
        var provider = new FakeEnrichment();
        var job = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id], ["fingerprint", "enrichment"], true), default);
        await Processor(store.Db, fp, provider).RunNextAsync(default);
        var result = await TargetedRetryService.GetAsync(store.Db, Owner, job.Id, default);
        Assert.Equal("qualificationRequired", result.Items[0].State); Assert.Equal("blocked", result.Items[1].State);
        Assert.Equal(0, provider.Calls); Assert.Null((await store.Db.Songs.AsNoTracking().SingleAsync()).Fingerprint);
        var stage = (await TargetedRetryService.StagesAsync(store.Db, Owner, song.Id, default)).Single();
        Assert.NotNull(stage.CandidateJson); Assert.NotNull(stage.SourceWarningCode);
        // A separately authorized name-based identification may succeed. It cannot clear source quality.
        var enriched = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id], ["enrichment"], true), default);
        var identify = new FakeEnrichment(async id => { await using var changed = NewDb(store); var s = await changed.Songs.SingleAsync(s => s.Id == id);
            s.EnrichmentStatus = EnrichmentStatus.Matched; await changed.SaveChangesAsync(); return EnrichmentOutcome.Matched; });
        await Processor(store.Db, new FakeFp(_ => Good), identify).RunNextAsync(default);
        Assert.Equal(1, identify.Calls); Assert.False(identify.LastFingerprintQualified); Assert.Equal("completed", (await TargetedRetryService.GetAsync(store.Db, Owner, enriched.Id, default)).State);
        var sourceWarning = (await TargetedRetryService.StagesAsync(store.Db, Owner, song.Id, default)).Single(s => s.Stage == "fingerprint");
        Assert.Equal(stage.SourceWarningCode, sourceWarning.SourceWarningCode); Assert.True((await store.Db.Songs.SingleAsync()).AutomationHeld);
        var refuse = await Assert.ThrowsAsync<ReviewPlanException>(() => TargetedRetryService.ResumeAutomationAsync(store.Db, files, Owner,
            new([song.Id], true, true, true), default)); Assert.Equal("qualification_required", refuse.Error.Code);
    }

    [PostgresTheory]
    [InlineData("missing")]
    [InlineData("id3Only")]
    [InlineData("empty")]
    public async Task MissingAndNonAudioInput_AreExplicitPersistentFailures(string kind)
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        if (kind == "missing") File.Delete(song.SourcePath);
        if (kind == "id3Only") { song.SourcePath = Path.Combine(root, "metadata-only.mp3"); await File.WriteAllBytesAsync(song.SourcePath, [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0]); }
        if (kind == "empty") await File.WriteAllBytesAsync(song.SourcePath, []);
        await store.Db.SaveChangesAsync();
        var job = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id]), default);
        var fp = new FakeFp(_ => Good); await Processor(store.Db, fp).RunNextAsync(default);
        Assert.Equal("failed", (await TargetedRetryService.GetAsync(store.Db, Owner, job.Id, default)).Items.Single().State);
        Assert.Equal(kind == "missing" ? 0 : 1, fp.Calls);
        await using var resumed = NewDb(store); Assert.Single((await TargetedRetryService.FailuresAsync(resumed, Owner, null, 0, 10, default)).Items);
        Assert.Null((await resumed.Songs.SingleAsync()).Fingerprint);
    }

    [PostgresFact]
    public async Task GoodResultsAreSkippedUnlessExplicitlyRecomputed_OthersStayValid()
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        var fp = new FakeFp(_ => Good);
        await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id]), default); await Processor(store.Db, fp).RunNextAsync(default);
        var second = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id]), default); await Processor(store.Db, fp).RunNextAsync(default);
        Assert.Equal(1, fp.Calls); Assert.Equal("already_valid", (await TargetedRetryService.GetAsync(store.Db, Owner, second.Id, default)).Items.Single().Code);
        await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id], recompute: true), default); await Processor(store.Db, fp).RunNextAsync(default);
        Assert.Equal(2, fp.Calls); Assert.Equal(2, (await store.Db.PipelineStageStates.SingleAsync()).Attempts);
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderBackoff_IsVisibleAndRespected_OverrideOnlyTouchesRequestedFailedAttempts(bool respect)
    {
        await using var store = await Store.CreateAsync(); var songs = await AddSongs(store.Db, 2);
        var next = DateTime.UtcNow.AddHours(1);
        store.Db.SongProviderAttempts.AddRange(songs.Select(s => new SongProviderAttempt { SongId = s.Id,
            Provider = EnrichmentProvider.SpotifyAPI, Status = ProviderAttemptStatus.RateLimited, AttemptedAtUtc = DateTime.UtcNow, RetryAfterUtc = next }));
        await store.Db.SaveChangesAsync();
        var request = Request([songs[0].Id], ["enrichment"], true, respect: respect);
        var job = await TargetedRetryService.CreateAsync(store.Db, files, Owner, request, default);
        var provider = new FakeEnrichment(); await Processor(store.Db, new FakeFp(_ => Good), provider).RunNextAsync(default);
        var result = (await TargetedRetryService.GetAsync(store.Db, Owner, job.Id, default)).Items.Single();
        Assert.Equal(respect ? "deferred" : "success", result.State); Assert.Equal(respect ? 0 : 1, provider.Calls);
        var untouched = await store.Db.SongProviderAttempts.AsNoTracking().SingleAsync(a => a.SongId == songs[1].Id);
        Assert.True(untouched.RetryAfterUtc > DateTime.UtcNow); Assert.Equal(ProviderAttemptStatus.RateLimited, untouched.Status);
        if (respect) Assert.NotNull((await TargetedRetryService.StagesAsync(store.Db, Owner, songs[0].Id, default)).Single().NextRetryAfterUtc);
    }

    [PostgresFact]
    public async Task ChangedSourceBeforeExecution_IsRefusedWithoutToolCall()
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        var job = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id]), default);
        var stamp = File.GetLastWriteTimeUtc(song.SourcePath); var data = await File.ReadAllBytesAsync(song.SourcePath); data[^1] ^= 1;
        await File.WriteAllBytesAsync(song.SourcePath, data); File.SetLastWriteTimeUtc(song.SourcePath, stamp);
        var fp = new FakeFp(_ => Good); await Processor(store.Db, fp).RunNextAsync(default);
        Assert.Equal(0, fp.Calls); Assert.Equal("input_changed", (await TargetedRetryService.GetAsync(store.Db, Owner, job.Id, default)).Items.Single().Code);
    }

    [PostgresFact]
    public async Task ForeignDeletedSyntheticAndInvalidSelections_AreRejectedBeforeHoldingAnyTrack()
    {
        await using var store = await Store.CreateAsync(); var songs = await AddSongs(store.Db, 3);
        songs[1].OwnerUserId = TestUsers.FriendId; songs[2].DeletedAtUtc = DateTime.UtcNow; await store.Db.SaveChangesAsync();
        foreach (var bad in new[] { songs[1].Id, songs[2].Id, 9999 })
            Assert.Equal(404, (await Assert.ThrowsAsync<ReviewPlanException>(() => TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([songs[0].Id, bad]), default))).StatusCode);
        Assert.Empty(await store.Db.TargetedRetryJobs.ToArrayAsync());
        Assert.False((await store.Db.Songs.AsNoTracking().SingleAsync(s => s.Id == songs[0].Id)).AutomationHeld);
        var job = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([songs[0].Id]), default);
        Assert.Equal(404, (await Assert.ThrowsAsync<ReviewPlanException>(() => TargetedRetryService.GetAsync(store.Db, TestUsers.FriendId, job.Id, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ReviewPlanException>(() => TargetedRetryService.StagesAsync(store.Db, TestUsers.FriendId, songs[0].Id, default))).StatusCode);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicateIds")]
    [InlineData("tooMany")]
    [InlineData("invalidStage")]
    [InlineData("implicitProviders")]
    [InlineData("extraProviders")]
    [InlineData("unboundedCopies")]
    [InlineData("wrongOrder")]
    public void RetryContractRejectsImplicitOrUnboundedEffects(string kind)
    {
        var request = Request([1]);
        request = kind switch {
            "empty" => request with { SongIds = [] }, "duplicateIds" => request with { SongIds = [1, 1] },
            "tooMany" => request with { SongIds = Enumerable.Range(1, 201).ToArray() },
            "invalidStage" => request with { Stages = ["scan"] }, "implicitProviders" => request with { Stages = ["enrichment"] },
            "extraProviders" => request with { AllowProviders = true }, "unboundedCopies" => request with { Stages = ["copies"] },
            _ => request with { Stages = ["enrichment", "fingerprint"], AllowProviders = true }
        };
        Assert.Equal(400, Assert.Throws<ReviewPlanException>(() => TargetedRetryService.Validate(request)).StatusCode);
    }

    [PostgresFact]
    public async Task ActiveWriterOrEnrichment_IsBusyWithoutAnyQueuedOrHeldRows()
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        await using (var guard = await ReviewWriteGuard.AcquireSongAsync(store.Db, song.Id, true, default))
        {
            var error = await Assert.ThrowsAsync<ReviewPlanException>(() => TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id]), default));
            Assert.Equal("writer_busy", error.Error.Code); Assert.Empty(await store.Db.TargetedRetryJobs.ToArrayAsync());
        }
        Assert.False((await store.Db.Songs.AsNoTracking().SingleAsync()).AutomationHeld);
        Assert.NotNull(await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id]), default));
    }

    [PostgresFact]
    public async Task ResumeAutomationRequiresExplicitAllEffectsAndFreshSuccessfulQuality()
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id]), default); await Processor(store.Db, new FakeFp(_ => Good)).RunNextAsync(default);
        Assert.Equal(400, (await Assert.ThrowsAsync<ReviewPlanException>(() => TargetedRetryService.ResumeAutomationAsync(store.Db, files, Owner, new([song.Id], true, false, true), default))).StatusCode);
        await TargetedRetryService.ResumeAutomationAsync(store.Db, files, Owner, new([song.Id], true, true, true), default);
        Assert.False((await store.Db.Songs.AsNoTracking().SingleAsync()).AutomationHeld);
    }

    [PostgresFact]
    public async Task SelectiveCopyRetry_DoesNotTouchSuccessfulOrUnselectedFailedTracks()
    {
        await using var store = await Store.CreateAsync(); var songs = await AddSongs(store.Db, 3);
        foreach (var song in songs) { song.EnrichmentStatus = EnrichmentStatus.Matched; song.TrackNumber = song.Id; }
        await store.Db.SaveChangesAsync();
        var scope = new AlbumReviewContextRequest([root], songs.Select(s => s.Id).ToArray());
        var context = await AlbumReviewContextReader.ReadAsync(store.Db, files, Owner, scope, default);
        var plan = await ReviewPlanService.PreviewAsync(store.Db, files, Resolver, Owner, new(scope, context.Revision,
            "Synthetic three-copy review", ["fixture:program"], songs.Select(s => new ReviewPlanTrackRequest(s.Id, false,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"Genre\":\"Reviewed\"}")!)).ToArray()), default);
        var applied = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        var failing = new CopyFailures(songs[0].Id);
        await new ReviewCopyProcessor(store.Db, files, new TagLibLibraryTagWriter(), Resolver, Tools, failing).RunNextAsync(default);
        var before = (await store.Db.ReviewCopyTracks.AsNoTracking().Where(t => t.SongId != songs[1].Id).OrderBy(t => t.SongId).ToArrayAsync());
        Assert.Equal("published", before[0].State); Assert.Equal("failed", before[1].State);
        var job = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([songs[1].Id], ["copies"], copy: applied.JobId), default);
        await Processor(store.Db, new FakeFp(_ => Good)).RunNextAsync(default);
        Assert.Equal("completed", (await TargetedRetryService.GetAsync(store.Db, Owner, job.Id, default)).State);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await store.Db.ReviewCopyTracks.AsNoTracking().Where(t => t.SongId != songs[1].Id).OrderBy(t => t.SongId).ToArrayAsync()));
        Assert.Equal("published", (await store.Db.ReviewCopyTracks.AsNoTracking().SingleAsync(t => t.SongId == songs[1].Id)).State);
        Assert.Equal("needsAttention", (await store.Db.ReviewCopyJobs.AsNoTracking().SingleAsync()).State);
    }

    [PostgresFact]
    public async Task Migration86_UpDownUp_PreservesCJobsAndSongs()
    {
        await using var store = await Store.CreateAsync(true); Assert.False(store.Db.Database.HasPendingModelChanges());
        await AddSongs(store.Db, 1);
        var migrations = (await store.Db.Database.GetAppliedMigrationsAsync()).ToArray(); Assert.Equal(86, migrations.Length);
        Assert.EndsWith("AddDurableTargetedRetries", migrations[^1]);
        await store.Db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
        Assert.Equal(1, await store.Db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"Songs\"").SingleAsync());
        await store.Db.GetService<IMigrator>().MigrateAsync(); Assert.False(store.Db.Database.HasPendingModelChanges());
        Assert.Equal(1, await store.Db.Songs.CountAsync()); Assert.Empty(await store.Db.TargetedRetryJobs.ToArrayAsync());
    }

    [PostgresFact]
    public async Task ProductionHttp_HostedWorkerAndOpenApi_ExposeScopedDurableResults()
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        var fp = new FakeFp(_ => Good); var provider = new FakeEnrichment();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddDbContext<MusicHoarderDbContext>(o => o.UseNpgsql(store.ConnectionString));
        builder.EnrichNpgsqlDbContext<MusicHoarderDbContext>();
        builder.Services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));
        builder.Services.AddSingleton(files); builder.Services.AddSingleton(Resolver);
        builder.Services.AddSingleton<IFpcalcService>(fp); builder.Services.AddSingleton<ITargetedEnrichmentRunner>(provider);
        builder.Services.AddSingleton<IReviewAudioCommandRunner>(Tools); builder.Services.AddSingleton<ILibraryTagWriter, TagLibLibraryTagWriter>();
        builder.Services.AddScoped<ReviewCopyProcessor>(); builder.Services.AddScoped<TargetedRetryProcessor>();
        builder.Services.AddHostedService<TargetedRetryBackgroundService>(); builder.Services.AddOpenApi();
        await using var app = builder.Build(); app.MapAlbumReviewEndpoints(); app.MapOpenApi(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var request = Request([song.Id]);
        using var created = await CreateWithBusyRetry();
        created.EnsureSuccessStatusCode();
        async Task<HttpResponseMessage> CreateWithBusyRetry()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                var response = await client.PostAsJsonAsync("/api/review/retries", request);
                if (response.StatusCode != System.Net.HttpStatusCode.Conflict || DateTime.UtcNow >= deadline) return response;
                Assert.Equal("writer_busy", (await response.Content.ReadFromJsonAsync<ReviewPlanError>())!.Code);
                response.Dispose(); await Task.Delay(25);
            }
        }
        var job = (await created.Content.ReadFromJsonAsync<TargetedRetryResult>())!;
        var until = DateTime.UtcNow.AddSeconds(20);
        while (job.State is "pending" or "running" && DateTime.UtcNow < until)
        { await Task.Delay(50); job = (await client.GetFromJsonAsync<TargetedRetryResult>($"/api/review/retries/{job.Id}"))!; }
        Assert.Equal("completed", job.State); Assert.Equal(1, fp.Calls); Assert.Equal(0, provider.Calls);
        var state = await client.GetFromJsonAsync<PipelineStageResult[]>($"/api/review/tracks/{song.Id}/stages"); Assert.Single(state!);
        var attempts = await client.GetFromJsonAsync<PipelineAttemptResult[]>($"/api/review/tracks/{song.Id}/attempts"); Assert.Single(attempts!);
        var failures = await client.GetFromJsonAsync<PipelineFailuresPage>("/api/review/failures?stage=fingerprint&limit=1"); Assert.Empty(failures!.Items);
        var api = await client.GetStringAsync("/openapi/v1.json"); Assert.Contains("TargetedRetryRequest", api); Assert.Contains("ResumeAutomationRequest", api);
        await app.StopAsync();
    }

    [PostgresFact]
    public async Task LegacyFingerprintFailure_IsPersistedAndExcludedAfterServiceRestart_EvenWithAnonymousBackgroundFilter()
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        var fp = new FakeFp(_ => FpcalcOutcome.Failure("synthetic_legacy_failure"));
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor());
        services.AddDbContext<MusicHoarderDbContext>(o => o.UseNpgsql(store.ConnectionString));
        services.AddSingleton(files); services.AddSingleton<IReviewAudioCommandRunner>(Tools);
        await using var container = services.BuildServiceProvider();
        var jobs = new JobManager(); var channel = new EnrichmentPipelineChannel(jobs, new EnrichmentProgressTracker()); var duplicates = new NoDuplicates();
        FingerprintBackgroundService Make() => new(container.GetRequiredService<IServiceScopeFactory>(), jobs, new FingerprintProgressTracker(),
            fp, duplicates, channel, new Available(), Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { FingerprintBatchSize = 10, FingerprintConcurrency = 1 }),
            NullLogger<FingerprintBackgroundService>.Instance);
        var batch = typeof(FingerprintBackgroundService).GetMethod("ProcessNextBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var count = typeof(FingerprintBackgroundService).GetMethod("CountPendingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = Make(); Assert.Equal(1, await (Task<int>)batch.Invoke(first, [Guid.NewGuid(), default(CancellationToken)])!);
        Assert.Equal(1, fp.Calls); Assert.Equal(0, channel.InFlight); Assert.Equal(0, duplicates.Calls);
        var restarted = Make(); Assert.Equal(0, await (Task<int>)count.Invoke(restarted, [default(CancellationToken)])!);
        Assert.Equal(0, await (Task<int>)batch.Invoke(restarted, [Guid.NewGuid(), default(CancellationToken)])!);
        Assert.Equal(1, fp.Calls); Assert.Single((await TargetedRetryService.FailuresAsync(store.Db, Owner, "fingerprint", 0, 10, default)).Items);
    }

    [PostgresFact]
    public async Task ExplicitFingerprintHold_ProtectsDuplicateFlagsAndNativeBuildSelection()
    {
        await using var store = await Store.CreateAsync(); var songs = await AddSongs(store.Db, 2);
        songs[0].IsDuplicate = true; songs[0].DuplicateOfId = songs[1].Id; await store.Db.SaveChangesAsync();
        await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([songs[0].Id]), default);
        await Processor(store.Db, new FakeFp(_ => Good)).RunNextAsync(default);
        var song = await store.Db.Songs.SingleAsync(s => s.Id == songs[0].Id);
        song.ClearDuplicate(); Assert.True(song.IsDuplicate); Assert.Equal(songs[1].Id, song.DuplicateOfId);
        song.MarkAsDuplicate(999); Assert.Equal(songs[1].Id, song.DuplicateOfId);
        Assert.Empty(await LibraryBuildQuery.BuildCandidates(store.Db.Songs.Where(s => s.Id == song.Id), null, 3).ToArrayAsync());
    }

    private sealed class NoDuplicates : IDuplicateDetectionService
    {
        internal int Calls { get; private set; }
        public Task<DuplicateDetectionResult> DetectDuplicatesAsync(CancellationToken ct = default)
        { Calls++; return Task.FromResult(new DuplicateDetectionResult(0, 0, 0, 0, TimeSpan.Zero)); }
    }
    private sealed class Available : IDirectoryAvailability
    {
        public DirectoryAvailabilitySnapshot Current => new(true, true, "/fixture/source", "/fixture/destination", DateTime.UtcNow);
        public Task<DirectoryAvailabilitySnapshot> ProbeNowAsync(CancellationToken ct = default) => Task.FromResult(Current);
    }

    [PostgresFact]
    public async Task ActualTruncatedSource_QualityWarningIsDurable_AndNoProviderRuns()
    {
        Assert.NotNull(FpcalcTools.Path);
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        using (var truncate = File.OpenWrite(song.SourcePath)) truncate.SetLength(truncate.Length - 100);
        var native = new FpcalcService(Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { FpcalcPath = FpcalcTools.Path! }), NullLogger<FpcalcService>.Instance);
        var provider = new FakeEnrichment();
        var job = await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id], ["fingerprint", "enrichment"], true), default);
        await Processor(store.Db, native, provider).RunNextAsync(default);
        var result = await TargetedRetryService.GetAsync(store.Db, Owner, job.Id, default);
        Assert.Equal("qualificationRequired", result.Items[0].State); Assert.Equal("blocked", result.Items[1].State);
        Assert.Equal(0, provider.Calls); Assert.Null((await store.Db.Songs.AsNoTracking().SingleAsync()).Fingerprint);
        await using var resumed = NewDb(store);
        var warning = (await TargetedRetryService.StagesAsync(resumed, Owner, song.Id, default)).Single();
        Assert.StartsWith("source_audio:", warning.SourceWarningCode); Assert.NotNull(warning.CandidateJson);
        var attempt = Assert.Single(await TargetedRetryService.AttemptsAsync(resumed, Owner, song.Id, null, 0, 10, default));
        Assert.Equal(3, attempt.ExitCode);
    }

    [PostgresFact]
    public async Task ChangedNativeFingerprintCannotReuseAnOldSuccessfulReceiptToResumeAutomation()
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        await TargetedRetryService.CreateAsync(store.Db, files, Owner, Request([song.Id]), default);
        await Processor(store.Db, new FakeFp(_ => Good)).RunNextAsync(default);
        (await store.Db.Songs.SingleAsync()).Fingerprint = "different-unqualified-value";
        await store.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ReviewPlanException>(() => TargetedRetryService.ResumeAutomationAsync(store.Db, files, Owner,
            new([song.Id], true, true, true), default));
        Assert.Equal("qualification_required", error.Error.Code);
        Assert.True((await store.Db.Songs.AsNoTracking().SingleAsync()).AutomationHeld);
    }

    [PostgresFact]
    public async Task TransactionRetry_CreatesOneJobAndOneHoldSet_WithoutLosingTheReceipt()
    {
        await using var store = await Store.CreateAsync(); var song = (await AddSongs(store.Db, 1)).Single();
        var probe = new RetryProbe();
        await using var retryDb = new MusicHoarderDbContext(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString, p => p.EnableRetryOnFailure())
            .AddInterceptors(probe).Options);
        var request = Request([song.Id]);
        var result = await TargetedRetryService.CreateAsync(retryDb, files, Owner, request, default);
        Assert.Equal(1, probe.Failures); Assert.Single(await retryDb.TargetedRetryJobs.ToArrayAsync());
        Assert.Single(await retryDb.TargetedRetryItems.ToArrayAsync()); Assert.True((await retryDb.Songs.SingleAsync()).AutomationHeld);
        Assert.Equal(result.Id, (await TargetedRetryService.CreateAsync(retryDb, files, Owner, request, default)).Id);
    }
    private sealed class RetryProbe : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        internal int Failures { get; private set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        {
            if (Failures == 0 && command.CommandText.Contains("INSERT INTO \"TargetedRetryJobs\""))
            {
                Failures++; Assert.NotNull(command.Transaction);
                throw new Npgsql.NpgsqlException("Synthetic retry before targeted commit", new TimeoutException("Synthetic timeout"));
            }
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }

    private async Task<SongMetadata[]> AddSongs(MusicHoarderDbContext db, int count)
    {
        var master = Path.Combine(root, "synthetic.flac");
        if (!File.Exists(master))
        {
            var output = await new ReviewAudioCommandRunner(AudioTools.Ffmpeg!).ExecuteAsync(false,
                ["-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=12", "-c:a", "flac", master], default);
            Assert.Equal(0, output.ExitCode);
        }
        var songs = new List<SongMetadata>();
        for (var n = 1; n <= count; n++)
        {
            var path = Path.Combine(root, $"source-{n}.flac"); File.Copy(master, path);
            var song = ReviewCopyTagRetagTests.Song(); song.OwnerUserId = Owner; song.SourcePath = path; song.FileName = Path.GetFileName(path);
            song.Extension = ".flac"; song.FileSizeBytes = new FileInfo(path).Length; song.TrackNumber = n; song.TotalTracks = count;
            song.EnrichmentStatus = EnrichmentStatus.Pending; song.LibraryBuildStatus = LibraryBuildStatus.Pending;
            db.Songs.Add(song); songs.Add(song);
        }
        await db.SaveChangesAsync(); return songs.ToArray();
    }
    private static MusicHoarderDbContext NewDb(Store store) => new(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString, pg => pg.EnableRetryOnFailure()).Options);
    private sealed class FakeFp(Func<string, FpcalcOutcome> outcome) : IFpcalcService
    {
        internal int Calls { get; private set; }
        public Task<FpcalcOutcome> GetFingerprintAsync(string path, int? maxLengthSeconds = null, CancellationToken ct = default) { Calls++; return Task.FromResult(outcome(path)); }
    }
    private sealed class FakeEnrichment(Func<int, Task<EnrichmentOutcome>>? run = null) : ITargetedEnrichmentRunner
    {
        internal int Calls { get; private set; }
        internal bool? LastFingerprintQualified { get; private set; }
        public Task<EnrichmentOutcome> RunAsync(int songId, bool fingerprintQualified, CancellationToken ct) { Calls++; LastFingerprintQualified = fingerprintQualified; return run?.Invoke(songId) ?? Task.FromResult(EnrichmentOutcome.Matched); }
    }
    private sealed class Points(string boundary) : ITargetedRetryCheckpoints
    { public Task ReachedAsync(string stage, int songId, CancellationToken ct) { if (stage == boundary) throw new OperationCanceledException("Synthetic interruption"); return Task.CompletedTask; } }
    private sealed class CopyFailures(int allowedId) : IReviewCopyCheckpoints
    { public Task ReachedAsync(string stage, ReviewCopyTrack track, CancellationToken ct) { if (stage == "beforeCopy" && track.SongId != allowedId) throw new IOException("Synthetic copy failure"); return Task.CompletedTask; } }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
