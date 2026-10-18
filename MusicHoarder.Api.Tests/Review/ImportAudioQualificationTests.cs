using System.IO.Abstractions;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Review.Retry;
using MusicHoarder.Api.Scanner;
using MusicHoarder.Api.Tests.Endpoints;
using MusicHoarder.Api.Tests.Library;
using Store = MusicHoarder.Api.Tests.Endpoints.AlbumReviewContextTests.TestDatabase;

namespace MusicHoarder.Api.Tests.Review;

public sealed class ImportAudioQualificationTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mh-import-audio-" + Guid.NewGuid().ToString("N"))).FullName;
    private const string CommentError = "Incorrect BOM value\nError reading comment frame, skipped\n";
    private IReviewAudioCommandRunner Tools => new ReviewAudioCommandRunner(AudioTools.Ffmpeg!);

    [Theory]
    [InlineData(CommentError, true)]
    [InlineData("[mp3 @ 0x123abc] Incorrect BOM value\n[mp3 @ 0x456def] Error reading comment frame, skipped\n", true)]
    [InlineData(CommentError + CommentError, true)]
    [InlineData("Incorrect BOM value", false)]
    [InlineData("Error reading comment frame, skipped", false)]
    [InlineData(CommentError + "decode_frame() failed", false)]
    [InlineData("invalid residual", false)]
    [InlineData("", false)]
    public void OnlyTheCompleteKnownCommentDiagnostic_IsRecognized(string errors, bool expected)
        => Assert.Equal(expected, ReviewAudioVerifier.KnownId3CommentDiagnostics(errors));

    [AudioToolsTheory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("flac", "flac")]
    public async Task DecodedSampleDurationAndHash_AgreeWithTheCompleteAudioProof(string extension, string codec)
    {
        var source = await Generate(extension, codec);
        var before = await Hash(source);
        var proof = await new ReviewAudioVerifier(Tools).VerifyAsync(source, source);
        Assert.True(proof.Verified, proof.FailureCode);
        var duration = await Tools.MeasureDurationAsync(source, Assert.Single(proof.Source!.Streams), default);
        Assert.Null(duration.FailureCode); Assert.InRange(duration.Seconds!.Value, 11.9, 12.1);
        Assert.True(duration.SampleFrames > 0); Assert.Equal(proof.Source.DecodedAudioSha256, duration.AudioSha256);
        Assert.Equal(before, await Hash(source));
    }

    [AudioToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualMalformedId3Comment_DoesNotHideMissingOrChangedAudio(bool missingAudio)
    {
        var source = await Generate("mp3", "libmp3lame");
        var original = await File.ReadAllBytesAsync(source);
        var offset = original.AsSpan(0, 3).SequenceEqual("ID3"u8)
            ? 10 + (original[6] << 21 | original[7] << 14 | original[8] << 7 | original[9]) : 0;
        byte[] body = [1, (byte)'e', (byte)'n', (byte)'g', 0x12, 0x34, 0, 0, 0x56, 0x78];
        byte[] frame = [(byte)'C', (byte)'O', (byte)'M', (byte)'M', 0, 0, 0, (byte)body.Length, 0, 0, .. body];
        byte[] header = [(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, (byte)frame.Length];
        var malformed = Path.Combine(root, "malformed.mp3");
        await File.WriteAllBytesAsync(malformed, [.. header, .. frame, .. (missingAudio ? [] : original[offset..])]);
        var before = await Hash(malformed);
        var raw = await ((ReviewAudioCommandRunner)Tools).ExecuteAsync(true,
            ["-v", "error", "-show_entries", "stream=codec_name", "-of", "json", malformed], default);
        if (!missingAudio) Assert.Contains("Incorrect BOM value", raw.Errors);
        var result = await new ReviewAudioVerifier(Tools).VerifyAsync(malformed, source);
        Assert.Equal(!missingAudio, result.Verified);
        if (!missingAudio) Assert.Equal(result.Source!.DecodedAudioSha256, result.Candidate!.DecodedAudioSha256);
        Assert.Equal(before, await Hash(malformed));
    }

    [AudioToolsTheory]
    [InlineData("mp3", "libmp3lame", false, true)]
    [InlineData("mp3", "libmp3lame", true, false)]
    [InlineData("flac", "flac", false, false)]
    public async Task MetadataAllowance_IsCodecBound_AndNeverHidesAnotherDiagnostic(string extension, string codec, bool extraError, bool expected)
    {
        var source = await Generate(extension, codec);
        var runner = new DiagnosticRunner(Tools, extraError);
        var result = await new ReviewAudioVerifier(runner).VerifyAsync(source, source);
        Assert.Equal(expected, result.Verified);
        if (!expected) Assert.Equal("audio_errors_reported", result.FailureCode);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRequalification_ResolvesOnlyCurrentWarningAndPreservesHistoricalReceipt(bool resolve)
    {
        await using var store = await Store.CreateAsync();
        var song = ReviewCopyTagRetagTests.Song(); song.SourcePath = await Generate("flac", "flac");
        // A STREAMINFO sample-total estimate can be wrong even when every encoded
        // frame decodes cleanly. Neither that estimate nor fake fpcalc's 77 s wins.
        var bytes = await File.ReadAllBytesAsync(song.SourcePath);
        var packed = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(18, 8));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), (packed & ~((1UL << 36) - 1)) | (99UL * 44100));
        await File.WriteAllBytesAsync(song.SourcePath, bytes);
        using (var tag = TagLib.File.Create(song.SourcePath)) Assert.InRange(tag.Properties.Duration.TotalSeconds, 98.9, 99.1);
        song.OwnerUserId = MusicHoarder.Api.Tests.Auth.TestUsers.OwnerId;
        song.Fingerprint = null; song.DurationSeconds = null; song.AutomationHeld = true;
        store.Db.Songs.Add(song); await store.Db.SaveChangesAsync();
        var hash = await Hash(song.SourcePath);
        await PipelineStageJournal.RecordAsync(store.Db, song, "fingerprint", "qualificationRequired",
            "fingerprint_duration_disagrees_with_source", hash, "{}", null, 0, null, true, default);
        var outcome = await FingerprintStage.ExecuteAsync(store.Db, song, song.SourcePath, new FileSystem(),
            new FakeFingerprint(77), Tools, Guid.NewGuid(), default, resolve);
        Assert.Equal("success", outcome.State); Assert.Equal(12, song.DurationSeconds); Assert.Equal(12000, song.DurationMs);
        Assert.True(song.AutomationHeld); // qualification alone never resumes automation
        var stage = await store.Db.PipelineStageStates.SingleAsync();
        Assert.Equal(resolve, stage.SourceWarningCode is null);
        var attempts = await store.Db.PipelineStageAttempts.OrderBy(x => x.AttemptedAtUtc).ToArrayAsync();
        Assert.Equal(2, attempts.Length); Assert.Equal("fingerprint_duration_disagrees_with_source", attempts[0].Code);
        using var evidence = JsonDocument.Parse(stage.CandidateJson!);
        Assert.Equal("decoded-pcm-samples-v1", evidence.RootElement.GetProperty("durationEvidence").GetProperty("method").GetString());
        Assert.Equal(resolve, evidence.RootElement.GetProperty("resolvedSourceWarning").ValueKind == JsonValueKind.Object);
        Assert.Equal(resolve, FingerprintStage.FreshAccepted(stage, song, hash));
    }

    [PostgresFact]
    public async Task FailedFreshAudioProof_CannotResolveAnExistingWarning()
    {
        await using var store = await Store.CreateAsync();
        var song = ReviewCopyTagRetagTests.Song(); song.SourcePath = Path.Combine(root, "empty.mp3");
        await File.WriteAllBytesAsync(song.SourcePath, new byte[512]);
        song.OwnerUserId = MusicHoarder.Api.Tests.Auth.TestUsers.OwnerId; song.Fingerprint = null;
        store.Db.Songs.Add(song); await store.Db.SaveChangesAsync();
        await PipelineStageJournal.RecordAsync(store.Db, song, "fingerprint", "qualificationRequired", "source_audio:audio_errors_reported",
            await Hash(song.SourcePath), "{}", null, 0, null, true, default);
        var result = await FingerprintStage.ExecuteAsync(store.Db, song, song.SourcePath, new FileSystem(),
            new FakeFingerprint(12), Tools, Guid.NewGuid(), default, true);
        Assert.NotEqual("success", result.State); Assert.Null(song.Fingerprint);
        Assert.NotNull((await store.Db.PipelineStageStates.SingleAsync()).SourceWarningCode);
        Assert.Equal(2, await store.Db.PipelineStageAttempts.CountAsync());
    }

    [PostgresFact]
    public async Task OrdinaryImport_DoesNotSilentlyAcceptADurationDisagreement()
    {
        await using var store = await Store.CreateAsync();
        var song = ReviewCopyTagRetagTests.Song(); song.SourcePath = await Generate("flac", "flac");
        song.OwnerUserId = MusicHoarder.Api.Tests.Auth.TestUsers.OwnerId; song.Fingerprint = null;
        store.Db.Songs.Add(song); await store.Db.SaveChangesAsync();
        var result = await FingerprintStage.ExecuteAsync(store.Db, song, song.SourcePath, new FileSystem(),
            new FakeFingerprint(77), Tools, null, default);
        Assert.Equal("qualificationRequired", result.State); Assert.Null(song.Fingerprint);
        Assert.NotNull((await store.Db.PipelineStageStates.SingleAsync()).SourceWarningCode);
    }

    [PostgresTheory]
    [InlineData("hash")]
    [InlineData("count")]
    [InlineData("source-change")]
    public async Task DurationQualification_RejectsInconsistentProofsAndSourceChanges(string scenario)
    {
        await using var store = await Store.CreateAsync();
        var song = ReviewCopyTagRetagTests.Song(); song.SourcePath = await Generate("flac", "flac");
        song.OwnerUserId = MusicHoarder.Api.Tests.Auth.TestUsers.OwnerId; song.Fingerprint = null;
        store.Db.Songs.Add(song); await store.Db.SaveChangesAsync();
        var result = await FingerprintStage.ExecuteAsync(store.Db, song, song.SourcePath, new FileSystem(),
            new FakeFingerprint(77), new MeasurementRunner(Tools, scenario), Guid.NewGuid(), default, true);
        Assert.NotEqual("success", result.State); Assert.Null(song.Fingerprint);
        Assert.Equal(scenario == "source-change" ? "source_changed_during_fingerprint" : "fingerprint_duration_disagrees_with_source", result.Code);
    }

    [Fact]
    public void WarningResolution_RequiresAnExplicitFingerprintStage()
    {
        var request = new TargetedRetryRequest(Guid.NewGuid(), [1], ["enrichment"], AllowProviders: true, ResolveSourceWarnings: true);
        Assert.Throws<MusicHoarder.Api.Review.ReviewPlanException>(() => TargetedRetryService.Validate(request));
    }

    [Fact]
    public void DefaultRequestJson_PreservesTheHistoricalIdempotencyBody()
    {
        var request = new TargetedRetryRequest(Guid.NewGuid(), [1], ["fingerprint"]);
        Assert.DoesNotContain("resolveSourceWarnings", JsonSerializer.Serialize(request, ReviewCopyJobs.Json));
        Assert.Contains("\"resolveSourceWarnings\":true", JsonSerializer.Serialize(request with { ResolveSourceWarnings = true }, ReviewCopyJobs.Json));
    }

    [UnixFact]
    public async Task DurationMeasurement_TimesOutAndHonoursCancellation()
    {
        var script = Path.Combine(root, "slow-ffmpeg");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var reader = new DecodedDurationReader(script, TimeSpan.FromMilliseconds(100));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("audio_tool_timeout", (await reader.ReadAsync("unused", new("mp3", 44100, 2), default)).FailureCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "A pipe reader delayed process timeout.");
        clock.Restart();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DecodedDurationReader(script).ReadAsync("unused", new("mp3", 44100, 2), cancel.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "A pipe reader delayed requested cancellation.");
    }

    private async Task<string> Generate(string extension, string codec)
    {
        var source = Path.Combine(root, Guid.NewGuid().ToString("N") + "." + extension);
        var result = await ((ReviewAudioCommandRunner)Tools).ExecuteAsync(false,
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=12", "-c:a", codec, source], default);
        Assert.Equal(0, result.ExitCode); return source;
    }
    private static async Task<string> Hash(string path)
    { await using var file = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(file)); }
    private sealed class FakeFingerprint(int duration) : IFpcalcService
    {
        public Task<FpcalcOutcome> GetFingerprintAsync(string path, int? maxLengthSeconds = null, CancellationToken ct = default)
            => Task.FromResult(FpcalcOutcome.Success(new(duration, FpcalcValidationTests.Fingerprint)));
    }
    private sealed class DiagnosticRunner(IReviewAudioCommandRunner inner, bool extraError) : IReviewAudioCommandRunner
    {
        public async Task<AudioCommandResult> RunAsync(bool probe, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            var result = await inner.RunAsync(probe, arguments, ct);
            return arguments.Contains("-version") ? result : result with { Errors = result.Errors + CommentError + (extraError ? "invalid residual\n" : "") };
        }
    }
    private sealed class MeasurementRunner(IReviewAudioCommandRunner inner, string scenario) : IReviewAudioCommandRunner
    {
        public Task<AudioCommandResult> RunAsync(bool probe, IReadOnlyList<string> args, CancellationToken ct) => inner.RunAsync(probe, args, ct);
        public async Task<DecodedDurationResult> MeasureDurationAsync(string path, ReviewAudioStream stream, CancellationToken ct)
        {
            var result = await inner.MeasureDurationAsync(path, stream, ct);
            if (scenario == "source-change") await File.AppendAllTextAsync(path, "changed", ct);
            return scenario == "hash" ? result with { AudioSha256 = new string('a', 64) }
                : scenario == "count" ? result with { SampleFrames = result.SampleFrames + 44100 } : result;
        }
    }
    public void Dispose() => Directory.Delete(root, true);
}
