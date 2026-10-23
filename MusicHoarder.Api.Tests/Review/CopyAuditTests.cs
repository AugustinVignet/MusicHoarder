using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Auth;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Review.Audit;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Endpoints;
using MusicHoarder.Api.Tests.Library;
using Store = MusicHoarder.Api.Tests.Endpoints.AlbumReviewContextTests.TestDatabase;

namespace MusicHoarder.Api.Tests.Review;

/// <summary>All healthy-audio witnesses use the native C verifier and synthetic fixtures.</summary>
public sealed class CopyAuditTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mh-copy-audit-" + Guid.NewGuid().ToString("N"))).FullName;
    private static readonly Guid Owner = TestUsers.OwnerId;
    private static readonly FileSystem Files = new();
    private string SourceRoot => Path.Combine(root, "source");
    private string CopyRoot => Path.Combine(root, "copies");
    private IOptions<MusicEnricherOptions> Config => Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = SourceRoot, DestinationDirectory = CopyRoot });
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static IReviewAudioCommandRunner Runner => new ReviewAudioCommandRunner(AudioTools.Ffmpeg ?? "ffmpeg");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private CopyAuditProcessor Worker(MusicHoarderDbContext db, IReviewAudioCommandRunner? runner = null, ICopyAuditCheckpoints? checkpoints = null)
        => new(db, Files, runner ?? Runner, Config, checkpoints);
    private static MusicHoarderDbContext Fresh(Store store) => new(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString, pg => pg.EnableRetryOnFailure()).Options);
    private async Task<SongMetadata> Add(MusicHoarderDbContext db, int id, string format = "flac")
    {
        Directory.CreateDirectory(SourceRoot); Directory.CreateDirectory(CopyRoot);
        var song = ReviewCopyTagRetagTests.Song(); song.Id = id; song.OwnerUserId = Owner;
        song.Title = $"Synthetic movement {id}"; song.TrackNumber = id; song.TotalTracks = 200;
        // A healthy legacy copy has no claimed value for plan-only extension tags.
        // Missing extensions with a claimed DB value are tested separately as issues.
        song.ReleaseTypePrimary = null; song.ReleaseDate = null;
        song.SourcePath = Path.Combine(SourceRoot, $"{id}.{format}"); song.DestinationPath = Path.Combine(CopyRoot, $"{id}.{format}");
        song.FileName = Path.GetFileName(song.SourcePath); song.Extension = "." + format;
        song.EnrichmentStatus = EnrichmentStatus.Matched; song.LibraryBuildStatus = LibraryBuildStatus.Done;
        song.IndexedAtUtc = Stamp; song.LastModifiedUtc = Stamp; song.LibraryBuiltAtUtc = Stamp;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence." + format), song.SourcePath);
        File.Copy(song.SourcePath, song.DestinationPath);
        await new TagLibLibraryTagWriter().WriteTagsAsync(song.DestinationPath, song, AlbumIdentity.FromSong(song));
        song.FileSizeBytes = new FileInfo(song.SourcePath).Length;
        db.Songs.Add(song); await db.SaveChangesAsync(); return song;
    }
    private static async Task<CopyAuditRequest> Request(MusicHoarderDbContext db, int[] ids, Guid? key = null)
    {
        var context = (await AlbumAuditReader.ReadAsync(db, Files, Owner, new(ids), default)).Packet;
        Assert.NotNull(context.PlanContext);
        return new(key ?? Guid.NewGuid(), context.PlanContext!.Scope, context.ContextRevision);
    }
    private static Task<CopyAuditJobResult> Get(MusicHoarderDbContext db, Guid id, int after = 0, int limit = 100)
        => CopyAuditJobs.GetAsync(db, Owner, id, after, limit, default);
    private static async Task Drain(CopyAuditProcessor worker, MusicHoarderDbContext db, Guid id, int max = 250)
    {
        for (var i = 0; i < max; i++)
        {
            var job = await Get(db, id);
            if (job.State is "completed" or "completedWithIssues" or "canceled") return;
            Assert.True(await worker.RunNextAsync(default));
        }
        Assert.Fail("Synthetic audit did not reach a bounded terminal state.");
    }
    private static async Task<string> MusicalSnapshot(MusicHoarderDbContext db) => JsonSerializer.Serialize(new
    {
        songs = await db.Songs.AsNoTracking().OrderBy(s => s.Id).ToArrayAsync(),
        changes = await db.SongMetadataChanges.AsNoTracking().OrderBy(c => c.Id).ToArrayAsync(),
        providers = await db.SongProviderAttempts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync(),
        plans = await db.ReviewPlans.AsNoTracking().OrderBy(p => p.Id).ToArrayAsync(),
        copyJobs = await db.ReviewCopyJobs.AsNoTracking().OrderBy(j => j.Id).ToArrayAsync(),
        retries = await db.TargetedRetryJobs.AsNoTracking().OrderBy(j => j.Id).ToArrayAsync()
    });
    private async Task<Dictionary<string, string>> FileHashes()
    {
        var result = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        { await using var input = File.OpenRead(path); result[path] = Convert.ToHexStringLower(await SHA256.HashDataAsync(input)); }
        return result;
    }

    [PostgresFact]
    public async Task FourteenExistingCopies_NoPlan_NoMusicalOrFileWrites_AndPagedGetDoesNoWork()
    {
        await using var store = await Store.CreateAsync();
        for (var i = 1; i <= 14; i++) await Add(store.Db, i);
        var before = await MusicalSnapshot(store.Db); var hashes = await FileHashes();
        var request = await Request(store.Db, Enumerable.Range(1, 14).ToArray());
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, request, default);
        Assert.Equal("queued", job.State); Assert.Equal(0, job.DecodeAttempts);
        if (!OperatingSystem.IsWindows())
            foreach (var path in hashes.Keys) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await using var background = new MusicHoarderDbContext(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString, p => p.EnableRetryOnFailure()).Options, new TestCurrentUserAccessor());
        await Drain(Worker(background), store.Db, job.Id);
        var finished = await Get(store.Db, job.Id); Assert.Equal("completed", finished.State);
        Assert.All(finished.Results, r => { Assert.Equal("verified", r.State); Assert.NotNull(r.Receipt); Assert.True(r.Receipt!.ContentVerified); Assert.NotNull(r.Receipt.CopyProof); Assert.NotNull(r.Receipt.SourceProof); });
        Assert.Equal("observedCurrent", finished.ScopeState); Assert.Equal(14, finished.Counts["verified"]);
        Assert.True(finished.BytesHashed > 0); Assert.True(finished.DecodeAttempts > 0);
        Assert.Equal(before, await MusicalSnapshot(store.Db)); Assert.Equal(hashes, await FileHashes());
        var ids = new List<int>(); int after = 0;
        do { var page = await Get(store.Db, job.Id, after, 3); ids.AddRange(page.Results.Select(r => r.SongId)); after = page.NextAfterSongId ?? 0; } while (after != 0);
        Assert.Equal(Enumerable.Range(1, 14), ids); Assert.Equal(14, await store.Db.CopyAuditAttempts.CountAsync());
        Assert.Equal(finished.BytesHashed, (await Get(store.Db, job.Id)).BytesHashed);
        Console.WriteLine($"F01/F23 tracks=14 bytesHashed={finished.BytesHashed} decodeAttempts={finished.DecodeAttempts} cacheHits={finished.CacheHits}");
    }

    [PostgresTheory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task RetaggedFormats_CurrentFullHashesAndToolVersionsGovernCache(string format)
    {
        await using var store = await Store.CreateAsync(); var song = await Add(store.Db, 1, format);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        await Drain(Worker(store.Db), store.Db, job.Id); var first = Assert.Single((await Get(store.Db, job.Id)).Results).Receipt!;
        Assert.True(first.State == "verified", JsonSerializer.Serialize(new { first.State, first.FailureCode, first.ExpectedDbRevision, first.ObservedDbRevision, first.ExpectedContextRevision, first.ObservedContextRevision, first.Stages })); Assert.NotEqual(first.SourceProof!.ContentSha256, first.CopyProof!.ContentSha256);
        var fresh = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        await Drain(Worker(store.Db), store.Db, fresh.Id); var cached = Assert.Single((await Get(store.Db, fresh.Id)).Results).Receipt!;
        Assert.Equal("verified", cached.State); Assert.True(cached.SourceCacheHit); Assert.True(cached.CopyCacheHit); Assert.Equal(0, cached.DecodeAttempts); Assert.True(cached.BytesHashed > 0);
        var upgraded = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        await Drain(Worker(store.Db, new VersionedRunner()), store.Db, upgraded.Id);
        var changed = Assert.Single((await Get(store.Db, upgraded.Id)).Results).Receipt!;
        Assert.Equal("verified", changed.State); Assert.False(changed.SourceCacheHit); Assert.False(changed.CopyCacheHit); Assert.True(changed.DecodeAttempts > 0);
        Assert.NotEqual(first.CopyProof.Version, changed.CopyProof!.Version);
        Console.WriteLine($"F02/F07/F09 format={format}, uncachedBytes={first.BytesHashed}, cachedBytes={cached.BytesHashed}, cachedDecodes={cached.DecodeAttempts}");
    }

    [PostgresFact]
    public async Task MissingCopyUnbuiltAndMissingSource_AreDistinctAndOtherTracksContinue()
    {
        await using var store = await Store.CreateAsync();
        for (var i = 1; i <= 4; i++) await Add(store.Db, i);
        File.Delete(store.Db.Songs.Local.Single(s => s.Id == 1).DestinationPath!);
        await store.Db.Songs.Where(s => s.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(x => x.DestinationPath, (string?)null).SetProperty(x => x.LibraryBuildStatus, LibraryBuildStatus.Pending));
        File.Delete(store.Db.Songs.Local.Single(s => s.Id == 3).SourcePath);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1, 2, 3, 4]), default);
        await Drain(Worker(store.Db), store.Db, job.Id); var result = await Get(store.Db, job.Id);
        Assert.Equal("completedWithIssues", result.State);
        Assert.Equal(new[] { "copy_missing", "not_built", "source_missing", null }, result.Results.Select(r => r.FailureCode));
        Assert.Equal("verified", result.Results[3].State); Assert.Equal(4, await store.Db.CopyAuditAttempts.CountAsync());
    }

    [PostgresFact]
    public async Task AllEditableTagsAndExplicitUnknownsAreReadBack_WithoutRepair()
    {
        await using var store = await Store.CreateAsync(); var song = await Add(store.Db, 1, "m4a");
        song.ReleaseTypePrimary = "album"; song.ReleaseDate = "2000-03-15";
        song.MusicBrainzReleaseId = "33333333-3333-4333-8333-333333333333";
        song.DiscNumber = null; song.ManuallyApprovedAtUtc = Stamp.AddTicks(1234567); song.IsManuallyApproved = true;
        store.Db.SongMetadataChanges.Add(new() { SongId = 1, FieldName = "DiscNumber", NewValue = null, Source = "ManualReview", AppliedAtUtc = song.ManuallyApprovedAtUtc, CreatedAtUtc = song.ManuallyApprovedAtUtc.Value });
        await store.Db.SaveChangesAsync();
        using (var file = TagLib.File.Create(song.DestinationPath!))
        { file.Tag.AlbumArtists = ["Wrong credit"]; file.Tag.Year = 1990; file.Tag.Track = 9; file.Save(); }
        var hashes = await FileHashes();
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        await Drain(Worker(store.Db), store.Db, job.Id); var receipt = Assert.Single((await Get(store.Db, job.Id)).Results).Receipt!;
        Assert.Equal("issues", receipt.State); Assert.Equal("tag_mismatch", receipt.FailureCode);
        var failures = Assert.Single(receipt.Stages, s => s.Stage == "tags").Fields!;
        foreach (var field in new[] { "AlbumArtist", "Year", "TrackNumber", "DiscNumber", "ReleaseTypePrimary", "ReleaseDate", "MusicBrainzReleaseId" }) Assert.Contains(field, failures);
        Assert.Equal(hashes, await FileHashes()); Assert.Null((await store.Db.Songs.AsNoTracking().SingleAsync()).DiscNumber);
    }

    [PostgresTheory]
    [InlineData("changedAudio")]
    [InlineData("truncated")]
    [InlineData("id3Only")]
    public async Task CorruptOrChangedAudio_IsNeverCertified(string kind)
    {
        await using var store = await Store.CreateAsync(); var song = await Add(store.Db, 1, kind == "id3Only" ? "mp3" : "flac");
        if (kind == "changedAudio")
        {
            var command = await new ReviewAudioCommandRunner(AudioTools.Ffmpeg ?? "ffmpeg").ExecuteAsync(false,
                ["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=880:duration=0.25", song.DestinationPath!], default);
            Assert.Equal(0, command.ExitCode); await new TagLibLibraryTagWriter().WriteTagsAsync(song.DestinationPath!, song, AlbumIdentity.FromSong(song));
        }
        else
        {
            if (kind == "id3Only") await File.WriteAllBytesAsync(song.SourcePath, [0x49,0x44,0x33,4,0,0,0,0,0,0]);
            else { var bytes = await File.ReadAllBytesAsync(song.SourcePath); await File.WriteAllBytesAsync(song.SourcePath, bytes[..Math.Max(42, bytes.Length / 2)]); }
            File.Copy(song.SourcePath, song.DestinationPath!, true);
        }
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        await Drain(Worker(store.Db), store.Db, job.Id); var result = Assert.Single((await Get(store.Db, job.Id)).Results);
        Assert.NotEqual("verified", result.State); Assert.False(result.Receipt!.ContentVerified);
        if (kind == "changedAudio") Assert.Equal("issues", result.State);
    }

    [PostgresFact]
    public async Task SameSizeAndMtimeDriftCannotReuseOldContentProof()
    {
        await using var store = await Store.CreateAsync(); var song = await Add(store.Db, 1);
        var first = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default); await Drain(Worker(store.Db), store.Db, first.Id);
        var old = Assert.Single((await Get(store.Db, first.Id)).Results).Receipt!;
        var stamp = File.GetLastWriteTimeUtc(song.DestinationPath!); var bytes = await File.ReadAllBytesAsync(song.DestinationPath!);
        bytes[^20] ^= 0x55; await File.WriteAllBytesAsync(song.DestinationPath!, bytes); File.SetLastWriteTimeUtc(song.DestinationPath!, stamp);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        await Drain(Worker(store.Db), store.Db, job.Id); var current = Assert.Single((await Get(store.Db, job.Id)).Results).Receipt!;
        Assert.False(current.CopyCacheHit); Assert.NotEqual("verified", current.State);
        Assert.Equal(bytes.Length, new FileInfo(song.DestinationPath!).Length); Assert.Equal(stamp, File.GetLastWriteTimeUtc(song.DestinationPath!));
        Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(Assert.Single((await Get(store.Db, first.Id)).Results).Receipt));
    }

    [PostgresFact]
    public async Task IdempotentKeysNormalizeIds_ConflictsAndStaleScopesCreateNothingPartial()
    {
        await using var store = await Store.CreateAsync(); await Add(store.Db, 1); await Add(store.Db, 2);
        var request = await Request(store.Db, [2, 1]);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, request, default);
        var reversed = request with { Scope = request.Scope with { TargetSongIds = [2, 1], Audit = request.Scope.Audit! with { MemberSongIds = [2, 1] } } };
        await using var fresh = Fresh(store);
        Assert.Equal(job.Id, (await CopyAuditJobs.CreateAsync(fresh, Files, Owner, reversed, default)).Id);
        Assert.Equal("request_key_reused", (await Assert.ThrowsAsync<ReviewPlanException>(() => CopyAuditJobs.CreateAsync(fresh, Files, Owner, request with { ContextRevision = new string('0', 64) }, default))).Error.Code);
        await store.Db.Songs.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "New DB generation"));
        Assert.Equal(job.Id, (await CopyAuditJobs.CreateAsync(fresh, Files, Owner, request, default)).Id);
        Assert.Equal("context_changed", (await Assert.ThrowsAsync<ReviewPlanException>(() => CopyAuditJobs.CreateAsync(fresh, Files, Owner, request with { RequestId = Guid.NewGuid() }, default))).Error.Code);
        Assert.Equal(1, await store.Db.CopyAuditJobs.CountAsync()); Assert.Empty(await store.Db.CopyAuditAttempts.ToListAsync());
    }

    [PostgresTheory]
    [InlineData("beforeAudio", "database")]
    [InlineData("beforeReceipt", "file")]
    [InlineData("afterFinalHash", "database")]
    [InlineData("afterFinalHash", "file")]
    [InlineData("beforeReceipt", "newNeighbor")]
    public async Task ConcurrentDriftNeverAcknowledgesMixedGenerations(string checkpoint, string change)
    {
        await using var store = await Store.CreateAsync(); var song = await Add(store.Db, 1);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        var once = false;
        var hook = new Hook(async (stage, _, ct) =>
        {
            if (stage != checkpoint || once) return; once = true;
            await using var outside = Fresh(store);
            if (change == "database") await outside.Songs.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "Concurrent generation"), ct);
            if (change == "file") { var stamp = File.GetLastWriteTimeUtc(song.DestinationPath!); var bytes = await File.ReadAllBytesAsync(song.DestinationPath!, ct); bytes[^20] ^= 0x33; await File.WriteAllBytesAsync(song.DestinationPath!, bytes, ct); File.SetLastWriteTimeUtc(song.DestinationPath!, stamp); }
            if (change == "newNeighbor") { var neighbor = new SongMetadata { Id = 100, OwnerUserId = Owner, SourcePath = Path.Combine(SourceRoot, "new-inbox.flac"), FileName = "new-inbox.flac", Extension = ".flac", FileSizeBytes = 0, IndexedAtUtc = Stamp, LastModifiedUtc = Stamp, EnrichmentStatus = EnrichmentStatus.Pending }; outside.Add(neighbor); await outside.SaveChangesAsync(ct); }
        });
        await Drain(Worker(store.Db, checkpoints: hook), store.Db, job.Id);
        var receipt = Assert.Single((await Get(store.Db, job.Id)).Results).Receipt!;
        Assert.True(once); Assert.Equal("stale", receipt.State); Assert.False(receipt.ContentVerified); Assert.Equal("stale", (await Get(store.Db, job.Id)).ScopeState);
    }

    [PostgresFact]
    public async Task ExplicitRetryCreatesOnlyOneChild_AndPreservesAllParentReceipts()
    {
        await using var store = await Store.CreateAsync(); var bad = await Add(store.Db, 1); await Add(store.Db, 2); await Add(store.Db, 3);
        File.Delete(bad.DestinationPath!); File.Delete(store.Db.Songs.Local.Single(s => s.Id == 3).DestinationPath!);
        var parent = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1, 2, 3]), default);
        await Drain(Worker(store.Db), store.Db, parent.Id); var before = await Get(store.Db, parent.Id);
        File.Copy(bad.SourcePath, bad.DestinationPath!); await new TagLibLibraryTagWriter().WriteTagsAsync(bad.DestinationPath!, bad, AlbumIdentity.FromSong(bad));
        var request = new CopyAuditRetryRequest(Guid.NewGuid(), [1]);
        var child = await CopyAuditJobs.RetryAsync(store.Db, Files, Owner, parent.Id, request, default);
        Assert.Equal(parent.Id, child.ParentJobId); Assert.Equal(new[] { 1 }, child.AuditedSongIds);
        Assert.Equal(child.Id, (await CopyAuditJobs.RetryAsync(store.Db, Files, Owner, parent.Id, request, default)).Id);
        await Drain(Worker(store.Db), store.Db, child.Id); Assert.Equal("verified", Assert.Single((await Get(store.Db, child.Id)).Results).State);
        var after = await Get(store.Db, parent.Id); Assert.Equal(JsonSerializer.Serialize(before.Results), JsonSerializer.Serialize(after.Results));
        Assert.Equal("invalid_retry", (await Assert.ThrowsAsync<ReviewPlanException>(() => CopyAuditJobs.RetryAsync(store.Db, Files, Owner, parent.Id, new(Guid.NewGuid(), [2]), default))).Error.Code);
    }


    [PostgresFact]
    public async Task TwoInstancesClaimOneTrack_ReleaseBetweenTracks_AndBoundFiftyTrackBacklog()
    {
        await using var store = await Store.CreateAsync(); for (var i = 1; i <= 50; i++) await Add(store.Db, i);
        var request = await Request(store.Db, Enumerable.Range(1, 50).ToArray());
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, request, default);
        var nextJob = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, request with { RequestId = Guid.NewGuid() }, default);
        await using var firstDb = Fresh(store); await using var secondDb = Fresh(store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new Hook(async (stage, id, ct) => { if (stage == "beforeAudio" && id == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } });
        var running = Worker(firstDb, checkpoints: hook).RunNextAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            Assert.False(await Worker(secondDb).RunNextAsync(default));
            var progress = await Get(store.Db, job.Id); Assert.Equal(1, progress.ActiveAttempts); Assert.Equal(2, progress.QueuedJobs);
            Assert.Equal("writer_busy", (await Get(store.Db, nextJob.Id)).WaitReason);
            Assert.Equal(1, await store.Db.CopyAuditAttempts.CountAsync());
        }
        finally { release.TrySetResult(); }
        Assert.True(await running);
        await using (var between = await ReviewWriteGuard.AcquireAsync(secondDb, false, default)) Assert.NotNull(between);
        await Drain(Worker(firstDb), store.Db, job.Id);
        var result = await Get(store.Db, job.Id); Assert.Equal(50, result.Counts["verified"]); Assert.All(result.Results, t => Assert.Equal(1, t.Attempts));
        await CopyAuditJobs.CancelAsync(store.Db, Owner, nextJob.Id, default);
        Console.WriteLine($"F14/F21 tracks=50 defaultActive=1 bytesHashed={result.BytesHashed} decodeAttempts={result.DecodeAttempts} cacheHits={result.CacheHits}");
    }

    [PostgresFact]
    public async Task ConcurrentCreationIsIdempotent_AndForeignObjectsAreUnavailable()
    {
        await using var store = await Store.CreateAsync(); await Add(store.Db, 1);
        var request = await Request(store.Db, [1]); await using var first = Fresh(store); await using var second = Fresh(store);
        var results = await Task.WhenAll(CopyAuditJobs.CreateAsync(first, Files, Owner, request, default), CopyAuditJobs.CreateAsync(second, Files, Owner, request, default));
        Assert.Equal(results[0].Id, results[1].Id); Assert.Equal(1, await store.Db.CopyAuditJobs.CountAsync());
        Assert.Equal(404, (await Assert.ThrowsAsync<ReviewPlanException>(() => CopyAuditJobs.GetAsync(store.Db, TestUsers.DemoId, results[0].Id, 0, 25, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<AlbumReviewReadException>(() => CopyAuditJobs.CreateAsync(store.Db, Files, TestUsers.DemoId, request, default))).StatusCode);
        var anonymous = await CopyAuditEndpoints.Create(request, store.Db, new TestCurrentUserAccessor(), Files, default);
        Assert.Equal(401, Assert.IsAssignableFrom<IStatusCodeHttpResult>(anonymous).StatusCode);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>(await CopyAuditEndpoints.Create(request, store.Db, new TestCurrentUserAccessor(TestCurrentUserAccessor.FriendUser), Files, default));
    }

    [PostgresTheory]
    [InlineData("internalSymlink")]
    [InlineData("escapingSymlink")]
    [InlineData("wrongCase")]
    [InlineData("unicodeShellCharacters")]
    public async Task PathsRemainExact_AndSymlinksCannotEscapeConfiguredRoots(string kind)
    {
        await using var store = await Store.CreateAsync(); var song = await Add(store.Db, 1);
        var original = song.DestinationPath!;
        if (kind == "internalSymlink" || kind == "escapingSymlink")
        {
            var target = kind == "internalSymlink" ? Path.Combine(CopyRoot, "actual.flac") : Path.Combine(root, "outside.flac");
            File.Move(original, target); File.CreateSymbolicLink(original, target);
        }
        if (kind == "wrongCase") { File.Move(original, Path.Combine(CopyRoot, "ExactCase.flac")); song.DestinationPath = Path.Combine(CopyRoot, "exactcase.flac"); }
        if (kind == "unicodeShellCharacters")
        {
            var name = "éxample ' \" ; $(false) `false`.flac";
            var path = Path.Combine(CopyRoot, name); File.Move(original, path); song.DestinationPath = path;
        }
        await store.Db.SaveChangesAsync();
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        await Drain(Worker(store.Db), store.Db, job.Id); var receipt = Assert.Single((await Get(store.Db, job.Id)).Results).Receipt!;
        if (kind is "internalSymlink" or "unicodeShellCharacters") Assert.Equal("verified", receipt.State);
        else { Assert.NotEqual("verified", receipt.State); Assert.Equal(kind == "wrongCase" ? "path_case_mismatch" : "symlink_outside_root", receipt.FailureCode); }
        Assert.Equal(song.DestinationPath, receipt.Copy.Path == "" ? song.DestinationPath : receipt.Copy.Path);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeToolCancellationAndTimeoutAreBounded_ReceiptsSurviveRestart(bool timeout)
    {
        await using var store = await Store.CreateAsync(); for (var i = 1; i <= 3; i++) await Add(store.Db, i);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1, 2, 3]), default);
        Assert.True(await Worker(store.Db).RunNextAsync(default));
        var firstReceipt = (await Get(store.Db, job.Id)).Results[0].Receipt;
        var marker = Path.Combine(root, "tool-started"); var tool = Path.Combine(root, "bounded-ffmpeg");
        await File.WriteAllTextAsync(tool, "#!/bin/sh\necho $$ > \"$MH_COPY_AUDIT_PROCESS_MARKER\"\nexec sleep 60\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var prior = Environment.GetEnvironmentVariable("MH_COPY_AUDIT_PROCESS_MARKER"); Environment.SetEnvironmentVariable("MH_COPY_AUDIT_PROCESS_MARKER", marker);
        await using var background = Fresh(store);
        var processor = new CopyAuditProcessor(background, Files, new ReviewAudioCommandRunner(tool), Config) { TrackTimeout = timeout ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(2) };
        try
        {
            var work = processor.RunNextAsync(default); var until = Stopwatch.StartNew();
            while (!File.Exists(marker) && until.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
            Assert.True(File.Exists(marker));
            if (!timeout) await CopyAuditJobs.CancelAsync(store.Db, Owner, job.Id, default);
            Assert.True(await work.WaitAsync(TimeSpan.FromSeconds(20)));
            var state = await Get(store.Db, job.Id);
            if (timeout) { Assert.Equal("audit_timeout", state.Results[1].FailureCode); Assert.NotNull(state.Results[1].NextAttemptAtUtc); Assert.Equal("running", state.State); await CopyAuditJobs.CancelAsync(store.Db, Owner, job.Id, default); }
            else Assert.Equal("canceled", state.State);
            Assert.Equal(JsonSerializer.Serialize(firstReceipt), JsonSerializer.Serialize(state.Results[0].Receipt));
            var processId = int.Parse(await File.ReadAllTextAsync(marker));
            try { using var process = Process.GetProcessById(processId); Assert.True(process.HasExited); } catch (ArgumentException) { }
            await using var restarted = Fresh(store); Assert.False(await Worker(restarted).RunNextAsync(default));
            Assert.Equal("canceled", (await Get(store.Db, job.Id)).State);
        }
        finally { Environment.SetEnvironmentVariable("MH_COPY_AUDIT_PROCESS_MARKER", prior); }
    }

    [PostgresFact]
    public async Task LostPublicationSessionCannotCommitPositiveReceipt_AndRecoveryPreservesAttemptHistory()
    {
        await using var store = await Store.CreateAsync(); await Add(store.Db, 1);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        var lost = false;
        var hook = new Hook(async (stage, _, ct) =>
        {
            if (stage != "beforeReceipt" || lost) return; lost = true;
            await using var connection = new NpgsqlConnection(store.ConnectionString); await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype='advisory' AND granted AND classid::bigint = @high AND objid::bigint = @low AND database=(SELECT oid FROM pg_database WHERE datname=current_database())", connection);
            const long key = 0x4d48525657524954;
            command.Parameters.AddWithValue("high", (long)(key >> 32)); command.Parameters.AddWithValue("low", (long)(key & 0xffffffff));
            await command.ExecuteNonQueryAsync(ct);
        });
        await Assert.ThrowsAnyAsync<Exception>(() => Worker(store.Db, checkpoints: hook).RunNextAsync(default));
        Assert.True(lost); Assert.DoesNotContain((await Get(store.Db, job.Id)).Results, r => r.State == "verified");
        var deferred = await Get(store.Db, job.Id); Assert.Equal("storage_or_lease_lost", deferred.Results[0].FailureCode); Assert.NotNull(deferred.Results[0].NextAttemptAtUtc);
        var past = CopyAuditJobs.Now.AddSeconds(-1);
        await store.Db.CopyAuditJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.NextAttemptAtUtc, past));
        await store.Db.CopyAuditTracks.Where(t => t.JobId == job.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.NextAttemptAtUtc, past));
        await using var resumed = Fresh(store); await Drain(Worker(resumed), store.Db, job.Id);
        var attempts = await CopyAuditJobs.AttemptsAsync(store.Db, Owner, job.Id, 1, 0, 25, default);
        Assert.Equal(new[] { "interrupted", "verified" }, attempts.Attempts.Select(a => a.State));
        Assert.Equal("storage_or_lease_lost", attempts.Attempts[0].Receipt!.FailureCode); Assert.False(attempts.Attempts[0].Receipt!.ContentVerified); Assert.NotNull(attempts.Attempts[1].Receipt);
    }


    [PostgresFact]
    public async Task ActualProcessDeathRecoversOnlyUnfinishedTracks_WithoutBrowserSessionOrExpiry()
    {
        await using var store = await Store.CreateAsync(); for (var i = 1; i <= 3; i++) await Add(store.Db, i);
        var request = await Request(store.Db, [1, 2, 3]); var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, request, default);
        await store.Db.CopyAuditJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.CreatedAtUtc, Stamp).SetProperty(j => j.UpdatedAtUtc, Stamp));
        var marker = Path.Combine(root, "child-claimed");
        using (var child = StartChild(store, marker, "interrupt"))
        {
            try
            {
                var timer = Stopwatch.StartNew(); while (!File.Exists(marker) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(100);
                Assert.True(File.Exists(marker), child.HasExited ? await child.StandardOutput.ReadToEndAsync() + await child.StandardError.ReadToEndAsync() : "Child did not reach persisted second claim.");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
        }
        var partial = await Get(store.Db, job.Id); Assert.Equal("verified", partial.Results[0].State); Assert.Equal("running", partial.Results[1].State);
        var first = JsonSerializer.Serialize(partial.Results[0].Receipt);
        using (var resumed = StartChild(store, marker, "resume"))
        {
            var output = resumed.StandardOutput.ReadToEndAsync(); var error = resumed.StandardError.ReadToEndAsync();
            try { await resumed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
            finally { if (!resumed.HasExited) { resumed.Kill(true); await resumed.WaitForExitAsync(); } }
            Assert.True(resumed.ExitCode == 0, await output + await error);
        }
        var completed = await Get(store.Db, job.Id); Assert.Equal("completed", completed.State);
        Assert.Equal(first, JsonSerializer.Serialize(completed.Results[0].Receipt)); Assert.Equal(1, completed.Results[0].Attempts); Assert.Equal(2, completed.Results[1].Attempts);
        Assert.Equal(job.Id, (await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, request, default)).Id);
        var history = await CopyAuditJobs.AttemptsAsync(store.Db, Owner, job.Id, 2, 0, 1, default);
        Assert.Equal("interrupted", Assert.Single(history.Attempts).State); Assert.Equal(1, history.NextAfterAttempt);
        Assert.Equal("verified", Assert.Single((await CopyAuditJobs.AttemptsAsync(store.Db, Owner, job.Id, 2, 1, 1, default)).Attempts).State);
    }

    private Process StartChild(Store store, string marker, string mode)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(CopyAuditTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=MusicHoarder.Api.Tests.Review.CopyAuditTests.ChildWorkerEntry");
        start.Environment["MH_COPY_AUDIT_CHILD"] = mode; start.Environment["MH_COPY_AUDIT_CHILD_DB"] = store.ConnectionString;
        start.Environment["MH_COPY_AUDIT_CHILD_SOURCE"] = SourceRoot; start.Environment["MH_COPY_AUDIT_CHILD_COPY"] = CopyRoot;
        start.Environment["MH_COPY_AUDIT_CHILD_MARKER"] = marker;
        return Process.Start(start)!;
    }

    // The parent test invokes this entry in a genuinely separate test-host process and kills
    // its process tree. It is not a Production Program/authentication acceptance witness.
    [PostgresFact]
    public async Task ChildWorkerEntry()
    {
        var mode = Environment.GetEnvironmentVariable("MH_COPY_AUDIT_CHILD"); if (mode is null) return;
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MH_COPY_AUDIT_CHILD_DB"));
        Assert.StartsWith("mh_review_test_", connection.Database); Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1" });
        await using var db = new MusicHoarderDbContext(new DbContextOptionsBuilder().UseNpgsql(connection.ConnectionString, p => p.EnableRetryOnFailure()).Options, new TestCurrentUserAccessor());
        var config = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions {
            SourceDirectory = Environment.GetEnvironmentVariable("MH_COPY_AUDIT_CHILD_SOURCE")!, DestinationDirectory = Environment.GetEnvironmentVariable("MH_COPY_AUDIT_CHILD_COPY")! });
        var hook = new Hook(async (stage, id, ct) =>
        {
            if (mode == "interrupt" && stage == "beforeAudio" && id == 2)
            { await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("MH_COPY_AUDIT_CHILD_MARKER")!, "persisted", ct); await Task.Delay(Timeout.Infinite, ct); }
        });
        var processor = new CopyAuditProcessor(db, Files, Runner, config, hook);
        var timer = Stopwatch.StartNew();
        while (await db.CopyAuditJobs.AnyAsync(j => j.State == "queued" || j.State == "running"))
        { Assert.True(timer.Elapsed < TimeSpan.FromSeconds(75)); if (!await processor.RunNextAsync(default)) await Task.Delay(100); }
    }

    [PostgresFact]
    public async Task Migration88_UpDownUp_AddsOnlyEmptyReleaseAndAcknowledgementTables()
    {
        await using var store = await Store.CreateAsync(migrate: true);
        Assert.False(store.Db.Database.HasPendingModelChanges());
        var all = store.Db.Database.GetMigrations().ToArray();
        Assert.Equal(88, all.Length);
        Assert.EndsWith("AddDurableCopyAudits", all[^2]);
        var migrator = store.Db.GetService<IMigrator>();
        await migrator.MigrateAsync(all[^2]);
        await Add(store.Db, 1);
        await migrator.MigrateAsync();
        Assert.Equal(1, await store.Db.Songs.CountAsync());
        Assert.Empty(await store.Db.ReviewDestinationReleases.ToListAsync());
        Assert.Empty(await store.Db.SongGradeAcknowledgements.ToListAsync());
        await migrator.MigrateAsync(all[^2]);
        Assert.Equal(1, await store.Db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"Songs\"").SingleAsync());
        await migrator.MigrateAsync();
        Assert.False(store.Db.Database.HasPendingModelChanges());
    }

    [PostgresFact]
    public async Task AdditiveMigrationFromSchema86PreservesRealPlanOutboxSnapshotsAndSongs()
    {
        await using var store = await Store.CreateAsync(migrate: true);
        var migrator = store.Db.GetService<IMigrator>(); await migrator.MigrateAsync("20261016223611_AddDurableTargetedRetries");
        await Add(store.Db, 1);
        var context = await AlbumReviewContextReader.ReadAsync(store.Db, Files, Owner, new([SourceRoot], [1]), default);
        var resolver = new DestinationPathResolver(Config);
        var preview = await ReviewPlanService.PreviewAsync(store.Db, Files, resolver, Owner,
            new(new([SourceRoot], [1]), context.Revision, "Synthetic migration continuity", ["fixture:booklet"],
                [new(1, false, new() { ["Genre"] = JsonSerializer.SerializeToElement("Fixture genre") })]), default);
        await ReviewPlanService.ApplyAsync(store.Db, Files, resolver, Owner, preview.Id, preview.Revision, default);
        var before = await MusicalSnapshot(store.Db);
        var copies = JsonSerializer.Serialize(await store.Db.ReviewCopyTracks.AsNoTracking().OrderBy(t => t.SongId).ToArrayAsync());
        await migrator.MigrateAsync();
        Assert.Equal(before, await MusicalSnapshot(store.Db)); Assert.Equal(copies, JsonSerializer.Serialize(await store.Db.ReviewCopyTracks.AsNoTracking().OrderBy(t => t.SongId).ToArrayAsync()));
        Assert.EndsWith("AddDestinationReleasesAndGradeAcknowledgements", (await store.Db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Empty(await store.Db.CopyAuditJobs.ToListAsync());
        var fresh = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        Assert.Equal("queued", fresh.State); Assert.Equal(1, await store.Db.ReviewCopyJobs.CountAsync());
    }

    [PostgresFact]
    public async Task HttpOpenApiAndReadOnlyGetExposeDurableScope_RejectClientPaths()
    {
        await using var store = await Store.CreateAsync(); await Add(store.Db, 1); var request = await Request(store.Db, [1]);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddDbContext<MusicHoarderDbContext>(o => o.UseNpgsql(store.ConnectionString, p => p.EnableRetryOnFailure()));
        builder.Services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));
        builder.Services.AddSingleton<IFileSystem>(Files); builder.Services.AddOpenApi();
        await using var app = builder.Build(); app.MapAlbumReviewEndpoints(); app.MapOpenApi(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using var created = await client.PostAsJsonAsync("/api/review/copy-audits", request); Assert.Equal(System.Net.HttpStatusCode.Accepted, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<CopyAuditJobResult>())!;
        Assert.Equal(AlbumAuditReader.Version, job.Scope.Audit!.Version); Assert.Equal(new[] { 1 }, job.AuditedSongIds);
        for (var i = 0; i < 3; i++) Assert.Equal("queued", (await client.GetFromJsonAsync<CopyAuditJobResult>(created.Headers.Location))!.State);
        Assert.Empty(await store.Db.CopyAuditAttempts.ToListAsync());
        var invalid = JsonSerializer.SerializeToNode(request, CopyAuditJobs.Json)!.AsObject(); invalid["sourcePath"] = "/outside/forbidden.flac";
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/review/copy-audits", invalid)).StatusCode);
        var openApi = await client.GetStringAsync("/openapi/v1.json");
        foreach (var value in new[] { "/api/review/copy-audits", "CopyAuditRequest", "CopyAuditJobResult", "CopyAuditReceipt", "CopyAuditRetryRequest" }) Assert.Contains(value, openApi);
        var evidenceRoot = Environment.GetEnvironmentVariable("MH_AUDIT_TEST_EVIDENCE");
        if (!string.IsNullOrEmpty(evidenceRoot)) { Directory.CreateDirectory(evidenceRoot); await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "f-openapi.json"), openApi); }
        await app.StopAsync();
    }

    [PostgresTheory]
    [InlineData("unavailable")]
    [InlineData("accessDenied")]
    [InlineData("invalidStreams")]
    public async Task UnverifiableFailuresHaveBoundedBackoff_AndDoNotBlockOtherTracks(string kind)
    {
        await using var store = await Store.CreateAsync(); await Add(store.Db, 1); await Add(store.Db, 2);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1, 2]), default);
        var hook = new Hook((stage, id, ct) => { if (kind == "accessDenied" && id == 1 && stage == "beforeAudio") throw new UnauthorizedAccessException("Synthetic denied read"); return Task.CompletedTask; });
        var runner = new FailureRunner(kind); var processor = Worker(store.Db, runner, hook);
        Assert.True(await processor.RunNextAsync(default));
        var first = (await Get(store.Db, job.Id)).Results[0]; Assert.NotEqual("verified", first.State);
        if (kind != "invalidStreams") Assert.NotNull(first.NextAttemptAtUtc);
        Assert.True(await Worker(store.Db).RunNextAsync(default)); Assert.Equal("verified", (await Get(store.Db, job.Id)).Results[1].State);
        if (kind != "invalidStreams")
        {
            for (var i = 0; i < 2; i++)
            {
                var past = CopyAuditJobs.Now.AddSeconds(-1);
                await store.Db.CopyAuditJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.NextAttemptAtUtc, past));
                await store.Db.CopyAuditTracks.Where(t => t.JobId == job.Id && t.SongId == 1).ExecuteUpdateAsync(s => s.SetProperty(t => t.NextAttemptAtUtc, past));
                Assert.True(await processor.RunNextAsync(default));
            }
            var final = await Get(store.Db, job.Id); Assert.Equal("completedWithIssues", final.State); Assert.Equal(3, final.Results[0].Attempts); Assert.Equal("unverifiable", final.Results[0].State); Assert.Null(final.Results[0].NextAttemptAtUtc);
        }
    }
    private sealed class FailureRunner(string kind) : IReviewAudioCommandRunner
    {
        public Task<AudioCommandResult> RunAsync(bool probe, IReadOnlyList<string> args, CancellationToken ct)
            => kind == "unavailable" ? Task.FromResult(new AudioCommandResult(null, "", "", "audio_tool_unavailable"))
                : kind == "invalidStreams" && probe && !args.SequenceEqual(["-version"])
                  ? Task.FromResult(new AudioCommandResult(0, "{\"streams\":[]}", "")) : Runner.RunAsync(probe, args, ct);
    }

    [PostgresFact]
    public async Task SharedContentCacheUpsertsAreSafeAgainstConcurrentNativeReaders()
    {
        await using var store = await Store.CreateAsync(); await Add(store.Db, 1);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default); await Drain(Worker(store.Db), store.Db, job.Id);
        var proof = JsonSerializer.Deserialize<ReviewAudioProof>((await store.Db.ReviewAudioCache.AsNoTracking().FirstAsync()).ProofJson, ReviewCopyJobs.Json)!;
        await store.Db.ReviewAudioCache.ExecuteDeleteAsync();
        await using var first = Fresh(store); await using var second = Fresh(store);
        await Task.WhenAll(new ReviewAudioProofStore(first).SaveAsync(proof, default), new ReviewAudioProofStore(second).SaveAsync(proof, default));
        Assert.Equal(1, await store.Db.ReviewAudioCache.CountAsync());
        Assert.Equal(proof.ContentSha256, (await new ReviewAudioProofStore(store.Db).FindAsync(proof.Version, proof.ContentSha256, default))!.ContentSha256);
    }

    [PostgresFact]
    public async Task SelectedCachedReferenceDriftMakesHealthyAudioStale()
    {
        await using var store = await Store.CreateAsync(); var song = await Add(store.Db, 1);
        var reference = new CanonicalAlbum { ArtistKey = "example ensemble", AlbumKey = "example program", DisplayArtist = song.AlbumArtist, DisplayTitle = song.Album,
            Status = CanonicalAlbumStatus.Fetched, FetchedAtUtc = Stamp, ResolvedTrackCount = 1,
            Tracks = [new() { DiscNumber = 1, TrackNumber = 1 }] };
        store.Db.Add(reference); await store.Db.SaveChangesAsync();
        var initial = (await AlbumAuditReader.ReadAsync(store.Db, Files, Owner, new([1]), default)).Packet;
        var chosen = Assert.Single(initial.References).Reference;
        var selected = (await AlbumAuditReader.ReadAsync(store.Db, Files, Owner, new([1], chosen), default)).Packet;
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, new(Guid.NewGuid(), selected.PlanContext!.Scope, selected.ContextRevision), default);
        var changed = false;
        var hook = new Hook(async (stage, _, ct) => { if (stage == "beforeReceipt" && !changed) { changed = true; await using var outside = Fresh(store); await outside.CanonicalAlbums.Where(a => a.Id == reference.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.TrackCountContested, true), ct); } });
        await Drain(Worker(store.Db, checkpoints: hook), store.Db, job.Id);
        Assert.True(changed); Assert.Equal("stale", Assert.Single((await Get(store.Db, job.Id)).Results).State);
    }

    [PostgresFact]
    public async Task ConfiguredDownloadRootIsAuditable_WithoutStartingDownloadsOrScans()
    {
        await using var store = await Store.CreateAsync(); var song = await Add(store.Db, 1);
        var downloads = Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName;
        var source = Path.Combine(downloads, "1.flac"); File.Move(song.SourcePath, source); song.SourcePath = source; await store.Db.SaveChangesAsync();
        var config = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = SourceRoot, DownloadDirectory = downloads, DestinationDirectory = CopyRoot });
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        await Drain(new CopyAuditProcessor(store.Db, Files, Runner, config), store.Db, job.Id);
        Assert.Equal("verified", Assert.Single((await Get(store.Db, job.Id)).Results).State);
        Assert.Empty(await store.Db.SongProviderAttempts.ToListAsync());
    }

    [PostgresFact]
    public async Task CachedUnavailableRootDefersWithoutTriggeringProbeSideEffects()
    {
        await using var store = await Store.CreateAsync(); await Add(store.Db, 1);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        var monitor = new ReadOnlyAvailability(new(false, true, SourceRoot, CopyRoot, CopyAuditJobs.Now));
        var worker = new CopyAuditProcessor(store.Db, Files, Runner, Config, availability: monitor);
        Assert.True(await worker.RunNextAsync(default));
        var result = Assert.Single((await Get(store.Db, job.Id)).Results);
        Assert.Equal("root_unavailable", result.FailureCode); Assert.NotNull(result.NextAttemptAtUtc); Assert.Equal(0, result.Receipt!.DecodeAttempts);
        Assert.Equal(0, monitor.Probes);
    }
    private sealed class ReadOnlyAvailability(DirectoryAvailabilitySnapshot state) : IDirectoryAvailability
    {
        public DirectoryAvailabilitySnapshot Current => state;
        internal int Probes { get; private set; }
        public Task<DirectoryAvailabilitySnapshot> ProbeNowAsync(CancellationToken ct = default)
        { Probes++; throw new InvalidOperationException("An audit must not trigger a discovery probe."); }
    }

    [PostgresFact]
    public async Task FileChangedDuringNativeDecodeIsStale_EvenWhenDecoderFailsAndMtimeIsRestored()
    {
        await using var store = await Store.CreateAsync(); var song = await Add(store.Db, 1);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        var changed = false;
        var runner = new DuringDecode(async () =>
        {
            changed = true; var stamp = File.GetLastWriteTimeUtc(song.DestinationPath!);
            var bytes = await File.ReadAllBytesAsync(song.DestinationPath!); bytes[^20] ^= 0x44;
            await File.WriteAllBytesAsync(song.DestinationPath!, bytes); File.SetLastWriteTimeUtc(song.DestinationPath!, stamp);
        });
        await Drain(Worker(store.Db, runner), store.Db, job.Id);
        var receipt = Assert.Single((await Get(store.Db, job.Id)).Results).Receipt!;
        Assert.True(changed); Assert.Equal("stale", receipt.State); Assert.False(receipt.ContentVerified); Assert.NotNull(receipt.CopyReadSha256);
    }
    private sealed class DuringDecode(Func<Task> mutation) : IReviewAudioCommandRunner
    {
        private bool changed;
        public async Task<AudioCommandResult> RunAsync(bool probe, IReadOnlyList<string> args, CancellationToken ct)
        {
            var work = Runner.RunAsync(probe, args, ct);
            if (!changed && !probe && args.Contains("pcm_s32le") && args.Any(a => a.EndsWith("/copies/1.flac", StringComparison.Ordinal)))
            { changed = true; await mutation(); }
            return await work;
        }
    }

    [PostgresFact]
    public async Task LostClaimCommitResponseDoesNotDuplicateLogicalAttempt()
    {
        await using var store = await Store.CreateAsync(); await Add(store.Db, 1);
        var job = await CopyAuditJobs.CreateAsync(store.Db, Files, Owner, await Request(store.Db, [1]), default);
        var failure = new LostCommitResponse();
        await using var workerDb = new MusicHoarderDbContext(new DbContextOptionsBuilder().UseNpgsql(store.ConnectionString, p => p.EnableRetryOnFailure()).AddInterceptors(failure).Options);
        Assert.True(await Worker(workerDb).RunNextAsync(default));
        Assert.True(failure.Injected); var result = Assert.Single((await Get(store.Db, job.Id)).Results);
        Assert.Equal("verified", result.State); Assert.Equal(1, result.Attempts); Assert.Equal(1, await store.Db.CopyAuditAttempts.CountAsync());
    }
    private sealed class LostCommitResponse : DbTransactionInterceptor
    {
        internal bool Injected { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (!Injected) { Injected = true; throw new NpgsqlException("Synthetic lost COMMIT response", new TimeoutException("Synthetic timeout")); }
            return Task.CompletedTask;
        }
    }

    private sealed class VersionedRunner : IReviewAudioCommandRunner
    {
        public async Task<AudioCommandResult> RunAsync(bool probe, IReadOnlyList<string> arguments, CancellationToken ct)
        { var value = await Runner.RunAsync(probe, arguments, ct); return arguments.SequenceEqual(["-version"]) ? value with { Output = value.Output + "\nSynthetic tool version change" } : value; }
    }
    private sealed class Hook(Func<string, int, CancellationToken, Task> action) : ICopyAuditCheckpoints
    { public Task ReachedAsync(string stage, int id, CancellationToken ct) => action(stage, id, ct); }
}
