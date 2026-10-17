using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Endpoints;
using MusicHoarder.Api.Tests.Library;
using Store = MusicHoarder.Api.Tests.Endpoints.AlbumReviewContextTests.TestDatabase;

namespace MusicHoarder.Api.Tests.Review;

public sealed class ReviewCopyJobTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mh-copy-job-" + Guid.NewGuid().ToString("N"));
    private readonly IFileSystem files = new FileSystem();
    private static readonly Guid Owner = TestUsers.OwnerId;
    public ReviewCopyJobTests() => Directory.CreateDirectory(root);
    private IDestinationPathResolver Resolver => new DestinationPathResolver(Microsoft.Extensions.Options.Options.Create(
        new MusicEnricherOptions { DestinationDirectory = Path.Combine(root, "destination") }));
    private IReviewAudioCommandRunner Runner => new ReviewAudioCommandRunner(AudioTools.Ffmpeg ?? "ffmpeg");
    private ReviewCopyProcessor Processor(MusicHoarderDbContext db, IReviewCopyCheckpoints? points = null, ILibraryTagWriter? writer = null)
        => new(db, files, writer ?? new TagLibLibraryTagWriter(), Resolver, Runner, points);

    [PostgresTheory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task LegacySnapshotApprovalSurvivesPostgresPrecision_CopyAndRollback(int extraTicks)
    {
        await using var store = await Store.CreateAsync();
        var song = await AddSong(store.Db);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        song = await store.Db.Songs.SingleAsync(); // Preview/Apply reload their own tracked instance.
        var track = await store.Db.ReviewCopyTracks.SingleAsync();
        var snapshot = ReviewCopyJobs.Read(track.AfterJson);
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234560 + extraTicks);
        // Controlled clock value, preserving every other approved value and its audit.
        // Production Apply emits this precision on Linux; PostgreSQL stores microseconds.
        song.ManuallyApprovedAtUtc = timestamp;
        foreach (var audit in await store.Db.SongMetadataChanges.Where(a => a.SongId == song.Id).ToArrayAsync())
        { audit.CreatedAtUtc = timestamp; audit.AppliedAtUtc = timestamp; }
        track.AfterJson = JsonSerializer.Serialize(snapshot with { ApprovedAtUtc = timestamp }, ReviewCopyJobs.Json);
        if (extraTicks != 0)
        {
            // A pre-C-R1 worker already refused its own legacy snapshot.
            track.State = "failed"; track.FailureCode = "plan_superseded";
            (await store.Db.ReviewCopyJobs.SingleAsync()).State = "needsAttention";
        }
        await store.Db.SaveChangesAsync();
        await using var reloadedDb = NewDb(store);
        var reloaded = await reloadedDb.Songs.SingleAsync();
        await ManualReviewMetadata.LoadAsync(reloadedDb, [reloaded]);
        Assert.True(ReviewCopyJobs.Matches(reloaded, track.AfterJson),
            "A PostgreSQL round trip must not invent a later correction when only timestamp precision changes.");
        if (extraTicks != 0) await ReviewCopyJobs.RetryAsync(reloadedDb, Owner, track.JobId, default);
        Assert.True(await Processor(reloadedDb).RunNextAsync(default));
        Assert.Equal("published", (await reloadedDb.ReviewCopyTracks.SingleAsync()).State);
        await using var rollbackDb = NewDb(store);
        await ReviewCopyRollback.RunAsync(rollbackDb, files, Owner, track.JobId, default);
        Assert.Equal("rolledBack", (await rollbackDb.ReviewCopyJobs.SingleAsync()).State);
    }

    [PostgresTheory]
    [InlineData("silence.mp3")]
    [InlineData("silence.flac")]
    [InlineData("silence.m4a")]
    [InlineData("silence.opus")]
    public async Task DurableApply_PublishesOnlyTargets_WithCompleteTagsAudioAndRetainedCopies(string fixture)
    {
        await using var store = await Store.CreateAsync();
        var song = await AddSong(store.Db, fixture);
        var original = File.ReadAllBytes(song.DestinationPath!);
        var previous = song.PreviousDestinationPath!;
        var plan = await Plan(store.Db, [song], new { Genre = "Example Genre", ReleaseTypePrimary = "album", ReleaseTypes = "album; live" });
        Assert.True(plan.CanApply);
        var key = Guid.NewGuid();
        var first = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default, key);
        Assert.NotNull(first.JobId);
        var again = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default, key);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(again)); Assert.Single(await store.Db.ReviewCopyJobs.ToArrayAsync());
        var auditCount = await store.Db.SongMetadataChanges.CountAsync();
        // New context simulates lost HTTP response and restart after commit, before queue execution.
        await using var resumed = NewDb(store);
        Assert.True(await Processor(resumed).RunNextAsync(default));
        store.Db.ChangeTracker.Clear();
        var job = await store.Db.ReviewCopyJobs.Include(j => j.Tracks).SingleAsync();
        var track = job.Tracks.Single();
        Assert.Equal("completed", job.State); Assert.Equal("published", track.State); Assert.Equal(1, track.Attempts);
        Assert.Equal(original, File.ReadAllBytes(track.BackupPath));
        Assert.True(File.Exists(previous));
        Assert.Equal(auditCount, await store.Db.SongMetadataChanges.CountAsync());
        var final = await store.Db.Songs.SingleAsync(); await ManualReviewMetadata.LoadAsync(store.Db, [final]);
        Assert.Empty(ReviewTagVerifier.Verify(final.DestinationPath!, final));
        Assert.Equal(LibraryBuildStatus.Done, final.LibraryBuildStatus); Assert.Equal(plan.Id, final.ReviewPlanId);
        Assert.True((await ReviewCopyJobs.GetAsync(store.Db, files, Owner, job.Id, default, Runner)).CopiesVerified);
        Assert.Equal("notAssessed", (await ReviewCopyJobs.GetAsync(store.Db, files, Owner, job.Id, default)).AlbumCompleteness);
        // Restarted worker does not rediscover or rebuild a completed job.
        Assert.False(await Processor(resumed).RunNextAsync(default));
    }

    [PostgresTheory]
    [InlineData("copied")]
    [InlineData("tagged")]
    [InlineData("verified")]
    [InlineData("publishing")]
    [InlineData("backedUp")]
    [InlineData("beforeRename")]
    [InlineData("renamed")]
    [InlineData("acknowledged")]
    public async Task InterruptedAtEachBoundary_ResumesWithoutReapplyingMetadata(string stage)
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed Genre" });
        await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        var auditCount = await store.Db.SongMetadataChanges.CountAsync();
        var points = new Points(stage, () => throw new OperationCanceledException("Synthetic process interruption"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Processor(store.Db, points).RunNextAsync(default));
        Assert.True(points.Fired);
        var track = await store.Db.ReviewCopyTracks.AsNoTracking().SingleAsync();
        Assert.True(File.Exists(track.OldPreviousPath));
        Assert.True(File.Exists(track.BackupPath) || File.Exists(track.OldDestinationPath));
        await using var resumed = NewDb(store);
        Assert.True(await Processor(resumed).RunNextAsync(default));
        store.Db.ChangeTracker.Clear();
        var job = await store.Db.ReviewCopyJobs.Include(j => j.Tracks).SingleAsync();
        Assert.Equal("completed", job.State); Assert.Equal(auditCount, await store.Db.SongMetadataChanges.CountAsync());
        Assert.True((await ReviewCopyJobs.GetAsync(store.Db, files, Owner, job.Id, default, Runner)).CopiesVerified);
        Assert.Equal(stage is "renamed" or "acknowledged" ? 1 : 2, job.Tracks.Single().Attempts);
    }

    [PostgresTheory]
    [InlineData("beforeCopy")]
    [InlineData("copied")]
    [InlineData("beforeRename")]
    public async Task FileWriteFailures_RetainBothOldCopies_RequireExplicitRetry(string stage)
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var old = File.ReadAllBytes(song.DestinationPath!); var previous = File.ReadAllBytes(song.PreviousDestinationPath!);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        Assert.True(await Processor(store.Db, new Points(stage, () => throw new IOException("Synthetic ENOSPC/rename denial"))).RunNextAsync(default));
        var job = await store.Db.ReviewCopyJobs.Include(j => j.Tracks).SingleAsync();
        Assert.Equal("needsAttention", job.State); Assert.Equal("failed", job.Tracks.Single().State);
        Assert.Equal(old, File.ReadAllBytes(song.DestinationPath!)); Assert.Equal(previous, File.ReadAllBytes(song.PreviousDestinationPath!));
        Assert.False((await ReviewCopyJobs.GetAsync(store.Db, files, Owner, job.Id, default)).CopiesVerified);
        Assert.False(await Processor(store.Db).RunNextAsync(default));
        await ReviewCopyJobs.RetryAsync(store.Db, Owner, receipt.JobId!.Value, default);
        Assert.True(await Processor(store.Db).RunNextAsync(default));
        Assert.Equal("completed", (await ReviewCopyJobs.OwnedAsync(store.Db, Owner, job.Id, default)).State);
    }

    [PostgresFact]
    public async Task IdempotencyKeyCannotBeReusedForAnotherPlan_AndNoSecondJobForSamePlan()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var plan = await Plan(store.Db, [song], new { Genre = "One" }); var key = Guid.NewGuid();
        var first = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default, key);
        Assert.Equal(first.JobId, (await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default, Guid.NewGuid())).JobId);
        var next = await Plan(store.Db, [song], new { Genre = "Two" });
        var error = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, next.Id, next.Revision, default, key));
        Assert.Equal("request_key_reused", error.Error.Code); Assert.Single(await store.Db.ReviewCopyJobs.ToArrayAsync());
        Assert.Equal("One", (await store.Db.Songs.AsNoTracking().SingleAsync()).Genre);
    }

    [PostgresFact]
    public async Task LaterCorrection_RefusesOldJobAndRollback_WithoutFileChanges()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var original = File.ReadAllBytes(song.DestinationPath!);
        var first = await Plan(store.Db, [song], new { Genre = "One" });
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, first.Id, first.Revision, default);
        var second = await Plan(store.Db, [song], new { Genre = "Two" });
        await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, second.Id, second.Revision, default);
        Assert.True(await Processor(store.Db).RunNextAsync(default));
        var job = await ReviewCopyJobs.OwnedAsync(store.Db, Owner, receipt.JobId!.Value, default);
        Assert.Equal("plan_superseded", job.Tracks.Single().FailureCode);
        var error = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewCopyRollback.RunAsync(store.Db, files, Owner, job.Id, default));
        Assert.Equal("later_correction", error.Error.Code); Assert.Equal(original, File.ReadAllBytes(song.DestinationPath!));
    }

    [PostgresFact]
    public async Task ContentChangeWithSameSizeAndMtime_InvalidatesCopyEvidenceAndNeverOverwrites()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await Processor(store.Db).RunNextAsync(default);
        Assert.True((await ReviewCopyJobs.GetAsync(store.Db, files, Owner, receipt.JobId!.Value, default, Runner)).CopiesVerified);
        var path = (await store.Db.ReviewCopyTracks.SingleAsync()).DestinationPath;
        var stamp = File.GetLastWriteTimeUtc(path); var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, stamp);
        Assert.False((await ReviewCopyJobs.GetAsync(store.Db, files, Owner, receipt.JobId.Value, default, Runner)).CopiesVerified);
        await ReviewCopyJobs.RetryAsync(store.Db, Owner, receipt.JobId.Value, default);
        await Processor(store.Db).RunNextAsync(default);
        Assert.Equal("published_content_changed", (await store.Db.ReviewCopyTracks.SingleAsync()).FailureCode);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [PostgresFact]
    public async Task RollbackRestoresOriginalMetadataAndFile_KeepsAuditsAndBothGenerations()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var original = File.ReadAllBytes(song.DestinationPath!);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await Processor(store.Db).RunNextAsync(default);
        var track = await store.Db.ReviewCopyTracks.SingleAsync(); var replacement = File.ReadAllBytes(track.DestinationPath);
        await ReviewCopyRollback.RunAsync(store.Db, files, Owner, receipt.JobId!.Value, default);
        await ReviewCopyRollback.RunAsync(store.Db, files, Owner, receipt.JobId.Value, default);
        store.Db.ChangeTracker.Clear(); var restored = await store.Db.Songs.SingleAsync();
        Assert.Null(restored.Genre); Assert.Null(restored.ReviewPlanId); Assert.False(restored.IsManuallyApproved);
        Assert.Equal(original, File.ReadAllBytes(track.DestinationPath));
        Assert.Equal(replacement, File.ReadAllBytes(track.BackupPath + ".replacement"));
        Assert.All(await store.Db.SongMetadataChanges.ToArrayAsync(), a => Assert.NotNull(a.RevertedAtUtc));
        Assert.Equal("rolledBack", (await store.Db.ReviewCopyJobs.SingleAsync()).State);
    }

    [PostgresFact]
    public async Task FourteenPositionsIncludingNeedsReview_PreserveEveryProgramMemberAndTake()
    {
        await using var store = await Store.CreateAsync();
        var songs = new List<SongMetadata>();
        for (var i = 1; i <= 14; i++) songs.Add(await AddSong(store.Db, number: i));
        songs[^1].EnrichmentStatus = EnrichmentStatus.NeedsReview; await store.Db.SaveChangesAsync();
        var scope = new AlbumReviewContextRequest([Path.Combine(root, "source")], songs.Select(s => s.Id).ToArray());
        var context = await AlbumReviewContextReader.ReadAsync(store.Db, files, Owner, scope, default);
        var request = new ReviewPlanRequest(scope, context.Revision, "Fourteen positions in fixture program", ["fixture:program:14"],
            songs.Select(s => new ReviewPlanTrackRequest(s.Id, s.EnrichmentStatus == EnrichmentStatus.NeedsReview,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"Genre\":\"Reviewed\"}")!)).ToArray());
        var plan = await ReviewPlanService.PreviewAsync(store.Db, files, Resolver, Owner, request, default);
        Assert.True(plan.CanApply); var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await using var restarted = NewDb(store);
        await Processor(restarted).RunNextAsync(default);
        var job = await ReviewCopyJobs.OwnedAsync(restarted, Owner, receipt.JobId!.Value, default);
        Assert.Equal("completed", job.State); Assert.Equal(14, job.Tracks.Count);
        Assert.Equal(14, job.Tracks.Select(t => t.DestinationPath).Distinct().Count());
        Assert.All(job.Tracks, t => { Assert.Equal("published", t.State); Assert.True(File.Exists(t.BackupPath)); });
        var final = await restarted.Songs.AsNoTracking().OrderBy(s => s.TrackNumber).ToArrayAsync();
        Assert.Equal(Enumerable.Range(1, 14), final.Select(s => s.TrackNumber!.Value));
        Assert.All(final, s => { Assert.False(s.IsDuplicate); Assert.Equal(EnrichmentStatus.Matched, s.EnrichmentStatus); });
        await using var rollbackDb = NewDb(store);
        await ReviewCopyRollback.RunAsync(rollbackDb, files, Owner, job.Id, default);
        Assert.Equal("rolledBack", (await rollbackDb.ReviewCopyJobs.SingleAsync()).State);
        Assert.All(await rollbackDb.Songs.AsNoTracking().ToArrayAsync(), s => {
            Assert.Null(s.Genre); Assert.False(s.IsManuallyApproved); Assert.Null(s.ReviewPlanId);
        });
        Assert.All(job.Tracks, t => {
            Assert.Equal(File.ReadAllBytes(t.BackupPath), File.ReadAllBytes(t.DestinationPath));
            Assert.True(File.Exists(t.BackupPath + ".replacement"));
        });
    }

    [PostgresFact]
    public async Task OwnerIsolationAndBusyRefusal_CannotChangeJobOrMetadata()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        await using (var guard = await ReviewWriteGuard.AcquireAsync(store.Db, true, default))
        {
            var error = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default));
            Assert.Equal("writer_busy", error.Error.Code); Assert.Empty(await store.Db.ReviewCopyJobs.ToArrayAsync());
        }
        var result = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        var other = Guid.NewGuid();
        Assert.Equal(404, (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewCopyJobs.GetAsync(store.Db, files, other, result.JobId!.Value, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewCopyJobs.RetryAsync(store.Db, other, result.JobId!.Value, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewCopyRollback.RunAsync(store.Db, files, other, result.JobId!.Value, default))).StatusCode);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctTakesAtSameSlot_RequireDismissedEvidence_AndKeepBothFiles(bool dismissed)
    {
        await using var store = await Store.CreateAsync();
        var first = await AddSong(store.Db, number: 1); var second = await AddSong(store.Db, number: 2);
        second.TrackNumber = 1; second.Title = "Example Alternate Take";
        second.DestinationPath = Resolver.ResolvePath(second);
        File.Copy(second.SourcePath, second.DestinationPath);
        if (dismissed) store.Db.SongDuplicateLinks.Add(new() { OwnerUserId = Owner, SongIdLow = first.Id,
            SongIdHigh = second.Id, Status = DuplicateLinkStatus.Dismissed, DismissedAtUtc = DateTime.UtcNow,
            DetectedAtUtc = DateTime.UtcNow, Confidence = DuplicateConfidence.Suspected, Reasons = DuplicateMatchReason.Metadata });
        await store.Db.SaveChangesAsync();
        var plan = await Plan(store.Db, [first, second], new { Genre = "Reviewed" });
        Assert.Equal(dismissed, plan.CanApply);
        if (!dismissed) { Assert.Contains(plan.Conflicts, c => c.Kind == "albumPosition"); return; }
        Assert.Contains(plan.Conflicts, c => c.Kind == "distinctTakesRetained");
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await Processor(store.Db).RunNextAsync(default);
        var job = await ReviewCopyJobs.OwnedAsync(store.Db, Owner, receipt.JobId!.Value, default);
        Assert.Equal("completed", job.State); Assert.All(job.Tracks, t => Assert.True(File.Exists(t.DestinationPath)));
        Assert.All(await store.Db.Songs.ToArrayAsync(), song => { Assert.False(song.IsDuplicate); song.MarkAsDuplicate(999); Assert.False(song.IsDuplicate); });
        Assert.Equal(DuplicateLinkStatus.Dismissed, (await store.Db.SongDuplicateLinks.SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task SameRecordingOnStudioAndCompilation_KeepsBothMemberships()
    {
        await using var store = await Store.CreateAsync();
        var studio = await AddSong(store.Db, number: 1); var compilation = await AddSong(store.Db, number: 2);
        studio.MusicBrainzId = compilation.MusicBrainzId = "11111111-1111-4111-8111-111111111111";
        compilation.Album = "Example Anthology"; compilation.IsCompilation = true; compilation.TrackNumber = 1;
        compilation.DestinationPath = Resolver.ResolvePath(compilation);
        Directory.CreateDirectory(Path.GetDirectoryName(compilation.DestinationPath)!); File.Copy(compilation.SourcePath, compilation.DestinationPath);
        await store.Db.SaveChangesAsync();
        var plan = await Plan(store.Db, [studio, compilation], new { Genre = "Reviewed" });
        Assert.True(plan.CanApply);
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await Processor(store.Db).RunNextAsync(default);
        Assert.Equal("completed", (await ReviewCopyJobs.OwnedAsync(store.Db, Owner, receipt.JobId!.Value, default)).State);
        Assert.Equal(2, (await store.Db.Songs.ToArrayAsync()).Select(s => s.Album).Distinct().Count());
        Assert.True(File.Exists(studio.DestinationPath)); Assert.True(File.Exists(compilation.DestinationPath));
    }

    [PostgresFact]
    public async Task CaseOnlyRename_PublishesExactNewSpelling_AndRollbackRestoresTheOldName()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var old = song.DestinationPath!; var original = File.ReadAllBytes(old);
        var plan = await Plan(store.Db, [song], new { Title = song.Title!.ToUpperInvariant() });
        Assert.True(plan.CanApply);
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await Processor(store.Db).RunNextAsync(default);
        var job = await ReviewCopyJobs.OwnedAsync(store.Db, Owner, receipt.JobId!.Value, default);
        Assert.Equal("completed", job.State);
        var target = job.Tracks.Single().DestinationPath;
        Assert.Contains(Path.GetFileName(target), Directory.GetFiles(Path.GetDirectoryName(target)!).Select(Path.GetFileName));
        await ReviewCopyRollback.RunAsync(store.Db, files, Owner, job.Id, default);
        Assert.Equal(original, File.ReadAllBytes(old));
        Assert.Contains(Path.GetFileName(old), Directory.GetFiles(Path.GetDirectoryName(old)!).Select(Path.GetFileName));
    }

    [PostgresFact]
    public async Task TagCorruptionBeforeVerification_CannotReplaceTheOldCopy()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var old = File.ReadAllBytes(song.DestinationPath!); string? staging = null;
        var points = new TrackPoints((stage, track) => { if (stage == "tagged") {
            staging = track.StagingPath; using var tag = TagLib.File.Create(staging); tag.Tag.Title = "Corrupt tag"; tag.Save(); } });
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await Processor(store.Db, points).RunNextAsync(default);
        Assert.NotNull(staging);
        Assert.StartsWith("tag_mismatch", (await store.Db.ReviewCopyTracks.SingleAsync()).FailureCode);
        Assert.Equal(old, File.ReadAllBytes(song.DestinationPath!));
    }
    private sealed class TrackPoints(Action<string, ReviewCopyTrack> action) : IReviewCopyCheckpoints
    {
        public Task ReachedAsync(string stage, ReviewCopyTrack track, CancellationToken ct) { action(stage, track); return Task.CompletedTask; }
    }

    [PostgresFact]
    public async Task DuplicateMarkedSlot_NeedsExplicitMembershipDecision_AndRollbackRestoresIt()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var keeper = await AddSong(store.Db, number: 2);
        song.IsDuplicate = true; song.DuplicateOfId = keeper.Id; await store.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ReviewPlanException>(() => Plan(store.Db, [song], new { Genre = "Reviewed" }));
        Assert.Equal("membership_decision_required", error.Error.Code);
        var scope = new AlbumReviewContextRequest([Path.Combine(root, "source")], [song.Id]);
        var context = await AlbumReviewContextReader.ReadAsync(store.Db, files, Owner, scope, default);
        var request = new ReviewPlanRequest(scope, context.Revision, "Preserve explicit album slot", ["fixture:membership"],
            [new(song.Id, false, new(), true)]);
        var plan = await ReviewPlanService.PreviewAsync(store.Db, files, Resolver, Owner, request, default);
        Assert.True(plan.CanApply);
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await Processor(store.Db).RunNextAsync(default);
        Assert.Equal("completed", (await store.Db.ReviewCopyJobs.SingleAsync()).State);
        Assert.False((await store.Db.Songs.AsNoTracking().SingleAsync(s => s.Id == song.Id)).IsDuplicate);
        await ReviewCopyRollback.RunAsync(store.Db, files, Owner, receipt.JobId!.Value, default);
        var restored = await store.Db.Songs.AsNoTracking().SingleAsync(s => s.Id == song.Id);
        Assert.True(restored.IsDuplicate); Assert.Equal(keeper.Id, restored.DuplicateOfId);
    }

    [PostgresFact]
    public async Task SourceContentChangedBeforeApply_EvenWithSameStat_RequiresNewPreview()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        var stamp = File.GetLastWriteTimeUtc(song.SourcePath); var bytes = File.ReadAllBytes(song.SourcePath); bytes[^1] ^= 1;
        File.WriteAllBytes(song.SourcePath, bytes); File.SetLastWriteTimeUtc(song.SourcePath, stamp);
        var error = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default));
        Assert.Equal("plan_stale", error.Error.Code); Assert.Empty(await store.Db.ReviewCopyJobs.ToArrayAsync());
        Assert.Empty(await store.Db.SongMetadataChanges.ToArrayAsync());
    }

    [PostgresFact]
    public async Task OneTrackPlan_DoesNotBuildOrRewriteItsUnselectedNeighbors_AndCacheSurvivesContextRestart()
    {
        await using var store = await Store.CreateAsync(); var target = await AddSong(store.Db); var neighbor = await AddSong(store.Db, number: 2);
        var neighborBefore = JsonSerializer.Serialize(await store.Db.Songs.AsNoTracking().SingleAsync(s => s.Id == neighbor.Id));
        var bytes = File.ReadAllBytes(neighbor.DestinationPath!);
        var plan = await Plan(store.Db, [target], new { Genre = "Reviewed" });
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await Processor(store.Db).RunNextAsync(default);
        Assert.Single(await store.Db.ReviewCopyTracks.ToArrayAsync());
        Assert.Equal(neighborBefore, JsonSerializer.Serialize(await store.Db.Songs.AsNoTracking().SingleAsync(s => s.Id == neighbor.Id)));
        Assert.Equal(bytes, File.ReadAllBytes(neighbor.DestinationPath!));
        Assert.True(await store.Db.ReviewAudioCache.CountAsync() >= 2);
        await using var resumed = NewDb(store);
        var track = await resumed.ReviewCopyTracks.SingleAsync();
        var proof = await new ReviewAudioVerifier(Runner, new ReviewAudioProofStore(resumed)).VerifyAsync(track.SourcePath, track.DestinationPath);
        Assert.True(proof.Verified); Assert.True(proof.SourceCacheHit); Assert.True(proof.CandidateCacheHit);
        Assert.True((await ReviewCopyJobs.GetAsync(resumed, files, Owner, receipt.JobId!.Value, default, Runner)).CopiesVerified);
    }

    [PostgresFact]
    public async Task ConcurrentWorkers_OnlyOneCanPublish_ApplyAndRetryStayBusy()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        var receipt = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await using var competing = NewDb(store);
        var checkedGate = false;
        var points = new AsyncPoints(async (stage, track) =>
        {
            if (stage != "verified") return;
            checkedGate = true;
            Assert.False(await Processor(competing).RunNextAsync(default));
            Assert.Equal("writer_busy", (await Assert.ThrowsAsync<ReviewPlanException>(() =>
                ReviewCopyJobs.RetryAsync(competing, Owner, receipt.JobId!.Value, default))).Error.Code);
            Assert.Equal("writer_busy", (await Assert.ThrowsAsync<ReviewPlanException>(() =>
                ReviewPlanService.ApplyAsync(competing, files, Resolver, Owner, plan.Id, plan.Revision, default))).Error.Code);
        });
        await Processor(store.Db, points).RunNextAsync(default);
        Assert.True(checkedGate); Assert.Equal(1, (await store.Db.ReviewCopyTracks.SingleAsync()).Attempts);
    }
    private sealed class AsyncPoints(Func<string, ReviewCopyTrack, Task> action) : IReviewCopyCheckpoints
    { public Task ReachedAsync(string stage, ReviewCopyTrack track, CancellationToken ct) => action(stage, track); }

    [PostgresTheory]
    [InlineData("backedUp", false)]
    [InlineData("beforeRename", true)]
    [InlineData("renamed", false)]
    public async Task RollbackOfUnacknowledgedPublication_RestoresOldBytesAndName(string boundary, bool caseOnly)
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var path = song.DestinationPath!; var bytes = File.ReadAllBytes(path);
        var fields = caseOnly ? (object)new { Title = song.Title!.ToUpperInvariant() } : new { Genre = "Reviewed" };
        var plan = await Plan(store.Db, [song], fields);
        var result = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Processor(store.Db,
            new Points(boundary, () => throw new OperationCanceledException("Synthetic crash"))).RunNextAsync(default));
        await using var resumed = NewDb(store);
        await ReviewCopyRollback.RunAsync(resumed, files, Owner, result.JobId!.Value, default);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Contains(Path.GetFileName(path), Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        Assert.Equal("rolledBack", (await resumed.ReviewCopyJobs.SingleAsync()).State);
    }

    [PostgresFact]
    public async Task CopyReceiptDatesSurvivePersistence_WithoutChangingJobOrGeneration()
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        var job = await store.Db.ReviewCopyJobs.Include(j => j.Tracks).SingleAsync();
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        job.CreatedAtUtc = at; job.UpdatedAtUtc = at.AddTicks(12); job.Tracks.Single().UpdatedAtUtc = at.AddTicks(24);
        await store.Db.SaveChangesAsync();
        var before = await ReviewCopyJobs.GetAsync(store.Db, files, Owner, job.Id, default);
        await using var restarted = NewDb(store);
        var after = await ReviewCopyJobs.GetAsync(restarted, files, Owner, job.Id, default);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(0, before.CreatedAtUtc.Ticks % 10); Assert.Equal(0, before.UpdatedAtUtc.Ticks % 10);
        Assert.Equal(0, before.Tracks.Single().UpdatedAtUtc.Ticks % 10);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalChangedByOneMicrosecond_IsStillALaterCorrection(bool published)
    {
        await using var store = await Store.CreateAsync(); var song = await AddSong(store.Db);
        var plan = await Plan(store.Db, [song], new { Genre = "Reviewed" });
        var applied = await ReviewPlanService.ApplyAsync(store.Db, files, Resolver, Owner, plan.Id, plan.Revision, default);
        if (published) await Processor(store.Db).RunNextAsync(default);
        await using var changed = NewDb(store);
        song = await changed.Songs.SingleAsync();
        song.ManuallyApprovedAtUtc = song.ManuallyApprovedAtUtc!.Value.AddTicks(10);
        await changed.SaveChangesAsync();
        var bytes = File.ReadAllBytes(song.DestinationPath!);
        await using var restarted = NewDb(store);
        var current = await restarted.Songs.SingleAsync(); await ManualReviewMetadata.LoadAsync(restarted, [current]);
        var track = await restarted.ReviewCopyTracks.SingleAsync();
        Assert.False(ReviewCopyJobs.Matches(current, track.AfterJson));
        if (!published)
        {
            Assert.True(await Processor(restarted).RunNextAsync(default));
            Assert.Equal("plan_superseded", (await restarted.ReviewCopyTracks.SingleAsync()).FailureCode);
        }
        var error = await Assert.ThrowsAsync<ReviewPlanException>(() => ReviewCopyRollback.RunAsync(restarted, files, Owner, applied.JobId!.Value, default));
        Assert.Equal("later_correction", error.Error.Code);
        Assert.Equal(bytes, File.ReadAllBytes(song.DestinationPath!));
    }

    [PostgresFact]
    public async Task ReleasedPublicationGate_CanBeImmediatelyReacquired()
    {
        await using var store = await Store.CreateAsync();
        await using var competing = NewDb(store);
        for (var n = 0; n < 30; n++)
        {
            var guard = await ReviewWriteGuard.AcquireAsync(store.Db, false, default);
            Assert.NotNull(guard);
            Assert.Null(await ReviewWriteGuard.AcquireAsync(competing, false, default));
            await guard.DisposeAsync();
            var next = await ReviewWriteGuard.AcquireAsync(competing, false, default);
            Assert.NotNull(next);
            await next.DisposeAsync();
        }
    }

    private async Task<SongMetadata> AddSong(MusicHoarderDbContext db, string fixture = "silence.flac", int number = 1)
    {
        Directory.CreateDirectory(Path.Combine(root, "source"));
        var source = Path.Combine(root, "source", $"example-{number}" + Path.GetExtension(fixture));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), source);
        var song = ReviewCopyTagRetagTests.Song(); song.Id = 0; song.OwnerUserId = Owner;
        song.SourcePath = source; song.Extension = Path.GetExtension(source); song.FileName = Path.GetFileName(source);
        song.TrackNumber = number; song.TotalTracks = 14; song.TotalDiscs = 1; song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.LibraryBuildStatus = LibraryBuildStatus.Done; song.FileSizeBytes = new FileInfo(source).Length;
        song.DestinationPath = Resolver.ResolvePath(song);
        Directory.CreateDirectory(Path.GetDirectoryName(song.DestinationPath)!); File.Copy(source, song.DestinationPath);
        song.PreviousDestinationPath = Path.Combine(root, $"previous-{number}" + song.Extension); File.Copy(source, song.PreviousDestinationPath);
        db.Songs.Add(song); await db.SaveChangesAsync(); return song;
    }
    private async Task<ReviewPlanPreview> Plan(MusicHoarderDbContext db, SongMetadata[] songs, object fields)
    {
        var scope = new AlbumReviewContextRequest([Path.Combine(root, "source")], songs.Select(s => s.Id).ToArray());
        var context = await AlbumReviewContextReader.ReadAsync(db, files, Owner, scope, default);
        return await ReviewPlanService.PreviewAsync(db, files, Resolver, Owner, new(scope, context.Revision, "Synthetic review",
            ["fixture:booklet"], songs.Select(s => new ReviewPlanTrackRequest(s.Id, false,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!)).ToArray()), default);
    }
    private static MusicHoarderDbContext NewDb(Store store) => new(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString, p => p.EnableRetryOnFailure()).Options);
    private sealed class Points(string stage, Action action) : IReviewCopyCheckpoints
    {
        internal bool Fired { get; private set; }
        public Task ReachedAsync(string name, ReviewCopyTrack track, CancellationToken ct)
        { if (name == stage && !Fired) { Fired = true; action(); } return Task.CompletedTask; }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
