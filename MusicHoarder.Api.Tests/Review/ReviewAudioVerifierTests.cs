using System.Security.Cryptography;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Tests.Library;

namespace MusicHoarder.Api.Tests.Review;

public sealed class ReviewAudioVerifierTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"mh-audio-proof-{Guid.NewGuid():N}")).FullName;
    public void Dispose() => Directory.Delete(root, true);

    [AudioToolsTheory]
    [InlineData("silence.mp3")]
    [InlineData("silence.flac")]
    [InlineData("silence.m4a")]
    public async Task ActualSourceAndRepeatedlyRetaggedCopy_PreserveEncodedAndDecodedAudio(string fixture)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var copy = Path.Combine(root, fixture); File.Copy(source, copy);
        var song = ReviewCopyTagRetagTests.Song(); var writer = new TagLibLibraryTagWriter();
        string sourceBefore;
        await using (var original = File.OpenRead(source))
            sourceBefore = Convert.ToHexStringLower(await SHA256.HashDataAsync(original));
        await writer.WriteTagsAsync(copy, song, AlbumIdentity.FromSong(song));
        await writer.WriteTagsAsync(copy, song, AlbumIdentity.FromSong(song));
        var result = await new ReviewAudioVerifier(new ReviewAudioCommandRunner(AudioTools.Ffmpeg!)).VerifyAsync(source, copy);
        Assert.True(result.Verified, $"{result.FailedStage}:{result.FailureCode}");
        Assert.NotNull(result.Source); Assert.NotNull(result.Candidate);
        Assert.Equal(result.Source.EncodedAudioSha256, result.Candidate.EncodedAudioSha256);
        Assert.Equal(result.Source.DecodedAudioSha256, result.Candidate.DecodedAudioSha256);
        Assert.NotEqual(result.Source.ContentSha256, result.Candidate.ContentSha256);
        await using var input = File.OpenRead(source);
        Assert.Equal(sourceBefore, Convert.ToHexStringLower(await SHA256.HashDataAsync(input)));
    }

    [AudioToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualMissingAudioOrMetadataOnly_IsNeverCertified(bool empty)
    {
        var path = Path.Combine(root, "incomplete.mp3");
        await File.WriteAllBytesAsync(path, empty ? [] : [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0]);
        var result = await new ReviewAudioVerifier(new ReviewAudioCommandRunner(AudioTools.Ffmpeg!)).VerifyAsync(path, path);
        Assert.False(result.Verified); Assert.Equal("source", result.FailedStage); Assert.Null(result.Source);
    }

    [AudioToolsTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ActualMultipleAudioStreams_MustAllSurvive(int candidateStreams)
    {
        var source = Path.Combine(root, "two-streams.m4a"); var candidate = Path.Combine(root, "candidate.m4a");
        var runner = new ReviewAudioCommandRunner(AudioTools.Ffmpeg!);
        var generated = await runner.ExecuteAsync(false,
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", "-map", "0:a", "-map", "0:a", "-c:a", "aac", source], default);
        Assert.Equal(0, generated.ExitCode);
        var arguments = new List<string> { "-nostdin", "-v", "error", "-i", source, "-map", "0:a:0" };
        if (candidateStreams == 2) arguments.AddRange(["-map", "0:a:1"]);
        arguments.AddRange(["-c:a", "copy", candidate]);
        Assert.Equal(0, (await runner.ExecuteAsync(false, arguments, default)).ExitCode);
        var result = await new ReviewAudioVerifier(runner).VerifyAsync(source, candidate);
        Assert.Equal(candidateStreams == 2, result.Verified);
        Assert.Equal(2, result.Source!.Streams.Count);
        Assert.Equal(candidateStreams, result.Candidate!.Streams.Count);
        if (candidateStreams == 1) Assert.Equal("stream_layout_changed", result.FailureCode);
    }

    [Fact]
    public async Task CachedProofsRequireContentAndToolVersion_EvenWhenPathSizeAndMtimeAreUnchanged()
    {
        var source = await Input("source", [1, 2, 3]); var copy = await Input("candidate", [4, 5, 6]);
        var runner = new FakeRunner(); var store = new ProofStore(); var verifier = new ReviewAudioVerifier(runner, store);
        Assert.True((await verifier.VerifyAsync(source, copy)).Verified); Assert.Equal(6, runner.AudioCommands);
        var cached = await verifier.VerifyAsync(source, copy);
        Assert.True(cached.Verified); Assert.True(cached.SourceCacheHit); Assert.True(cached.CandidateCacheHit);
        Assert.Equal(6, runner.AudioCommands);
        var stamp = File.GetLastWriteTimeUtc(source); await File.WriteAllBytesAsync(source, [7, 8, 9]); File.SetLastWriteTimeUtc(source, stamp);
        var changed = await verifier.VerifyAsync(source, copy);
        Assert.True(changed.Verified); Assert.False(changed.SourceCacheHit); Assert.True(changed.CandidateCacheHit);
        Assert.Equal(9, runner.AudioCommands);
        runner.ToolVersion = "changed-tool-version";
        var upgraded = await verifier.VerifyAsync(source, copy);
        Assert.True(upgraded.Verified); Assert.False(upgraded.SourceCacheHit); Assert.False(upgraded.CandidateCacheHit);
        Assert.Equal(15, runner.AudioCommands);
    }

    [Theory]
    [InlineData("encoded-exit", "audio_tool_failed")]
    [InlineData("decoded-exit", "audio_tool_failed")]
    [InlineData("stderr", "audio_errors_reported")]
    [InlineData("json", "invalid_tool_output")]
    [InlineData("hash", "invalid_audio_hash_output")]
    [InlineData("zero-hash", "invalid_audio_hash_output")]
    [InlineData("timeout", "audio_tool_timeout")]
    [InlineData("overflow", "tool_output_limit")]
    [InlineData("no-stream", "invalid_audio_streams")]
    public async Task FailedOrAmbiguousToolOutput_IsExplicitAndNeverCached(string scenario, string expected)
    {
        var source = await Input("source", [1, 2, 3]); var copy = await Input("candidate", [4, 5, 6]);
        var store = new ProofStore(); var runner = new FakeRunner { Scenario = scenario };
        var result = await new ReviewAudioVerifier(runner, store).VerifyAsync(source, copy);
        Assert.False(result.Verified); Assert.Equal(expected, result.FailureCode); Assert.Equal("source", result.FailedStage);
        Assert.Empty(store.Proofs);
    }

    [Theory]
    [InlineData("encoded", "encoded_audio_changed")]
    [InlineData("decoded", "decoded_audio_changed")]
    [InlineData("layout", "stream_layout_changed")]
    public async Task CandidateChangedInAudioOrStreamLayout_IsRefused(string scenario, string expected)
    {
        var source = await Input("source", [1, 2, 3]); var copy = await Input("candidate", [4, 5, 6]);
        var result = await new ReviewAudioVerifier(new FakeRunner { Scenario = scenario }).VerifyAsync(source, copy);
        Assert.False(result.Verified); Assert.Equal("comparison", result.FailedStage); Assert.Equal(expected, result.FailureCode);
    }

    [Fact]
    public async Task MutationDuringVerification_AndMissingFile_AreNotSuccessfulProofs()
    {
        var source = await Input("source", [1, 2, 3]); var copy = await Input("candidate", [4, 5, 6]);
        var runner = new FakeRunner { OnDecode = () => File.WriteAllBytes(source, [7, 8, 9]) };
        var store = new ProofStore(); var result = await new ReviewAudioVerifier(runner, store).VerifyAsync(source, copy);
        Assert.False(result.Verified); Assert.Equal("file_changed_during_verification", result.FailureCode); Assert.Empty(store.Proofs);
        File.Delete(source);
        Assert.Equal("file_missing", (await new ReviewAudioVerifier(new FakeRunner()).VerifyAsync(source, copy)).FailureCode);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReviewAudioVerifier(new FakeRunner()).VerifyAsync(source, copy, cancel.Token));
    }

    [Fact]
    public async Task SourceChangedWhileCandidateIsChecked_IsRefusedAtTheComparisonBoundary()
    {
        var source = await Input("source", [1, 2, 3]); var copy = await Input("candidate", [4, 5, 6]);
        var runner = new FakeRunner { OnCandidateDecode = () => File.WriteAllBytes(source, [7, 8, 9]) };
        var result = await new ReviewAudioVerifier(runner).VerifyAsync(source, copy);
        Assert.False(result.Verified); Assert.Equal("comparison", result.FailedStage);
        Assert.Equal("file_changed_after_verification", result.FailureCode);
    }

    [Fact]
    public async Task ProcessRunner_MissingExecutableReturnsAUsefulFailure()
    {
        var command = await new ReviewAudioCommandRunner(Path.Combine(root, "missing-ffmpeg"))
            .ExecuteAsync(false, ["-version"], default);
        Assert.Equal("audio_tool_unavailable", command.Failure);
    }

    [UnixFact]
    public async Task ProcessRunner_BoundsBothPipesAndTimesOutAStalledProcess()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var script = Path.Combine(root, "fake-ffmpeg");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nif [ \"$1\" = slow ]; then sleep 30; else head -c 40000 /dev/zero; head -c 40000 /dev/zero >&2; fi\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runner = new ReviewAudioCommandRunner(script, TimeSpan.FromSeconds(3));
        var output = await runner.ExecuteAsync(false, ["noisy"], default);
        Assert.True(output.Truncated); Assert.Equal(32768, output.Output.Length); Assert.Equal(32768, output.Errors.Length);
        var stalled = await new ReviewAudioCommandRunner(script, TimeSpan.FromMilliseconds(100)).ExecuteAsync(false, ["slow"], default);
        Assert.Equal("audio_tool_timeout", stalled.Failure);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteAsync(false, ["slow"], cancel.Token));
    }

    private async Task<string> Input(string name, byte[] bytes)
    { var path = Path.Combine(root, name); await File.WriteAllBytesAsync(path, bytes); return path; }
    private sealed class ProofStore : IReviewAudioProofStore
    {
        public List<ReviewAudioProof> Proofs { get; } = [];
        public Task<ReviewAudioProof?> FindAsync(string version, string contentSha256, CancellationToken ct) =>
            Task.FromResult(Proofs.FirstOrDefault(p => p.Version == version && p.ContentSha256 == contentSha256));
        public Task SaveAsync(ReviewAudioProof proof, CancellationToken ct) { Proofs.Add(proof); return Task.CompletedTask; }
    }
    private sealed class FakeRunner : IReviewAudioCommandRunner
    {
        public string? Scenario { get; init; }
        public string ToolVersion { get; set; } = "synthetic-tool-version";
        public int AudioCommands { get; private set; }
        public Action? OnDecode { get; init; }
        public Action? OnCandidateDecode { get; init; }
        public Task<AudioCommandResult> RunAsync(bool probe, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (arguments.Contains("-version")) return Task.FromResult(new AudioCommandResult(0, ToolVersion, ""));
            AudioCommands++;
            if (probe)
            {
                var text = Scenario == "json" ? "not-json" : Scenario == "no-stream" ? "{\"streams\":[]}"
                    : $$"""{"streams":[{"index":0,"codec_name":"mp3","sample_rate":"44100","channels":{{(Scenario == "layout" && arguments.Last().EndsWith("candidate") ? 1 : 2)}}}]}""";
                return Task.FromResult(new AudioCommandResult(0, text, ""));
            }
            var decoding = arguments.Contains("pcm_s32le");
            if (decoding) OnDecode?.Invoke();
            if (decoding && arguments.Any(a => a.EndsWith("candidate"))) OnCandidateDecode?.Invoke();
            var hash = new string('a', 64);
            if (Scenario == "hash") hash = "bad-hash";
            if (Scenario == "zero-hash") hash = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";
            if (arguments.Any(a => a.EndsWith("candidate")) && ((Scenario == "encoded" && !decoding) || (Scenario == "decoded" && decoding))) hash = new string('b', 64);
            return Task.FromResult(new AudioCommandResult(
                (Scenario == "encoded-exit" && !decoding) || (Scenario == "decoded-exit" && decoding) ? 3 : 0,
                $"0,a,SHA256={hash}\n", Scenario == "stderr" ? "synthetic decoding error" : "",
                Scenario == "timeout" ? "audio_tool_timeout" : null, Scenario == "overflow"));
        }
    }
}

public sealed class AudioToolsTheoryAttribute : TheoryAttribute
{
    public AudioToolsTheoryAttribute() { if (AudioTools.Ffmpeg is null || AudioTools.Ffprobe is null) Skip = "Install ffmpeg/ffprobe for real synthetic-audio verification."; }
}
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Unix process-tree and pipe fixture."; }
}
internal static class AudioTools
{
    public static string? Ffmpeg => Find("ffmpeg");
    public static string? Ffprobe => Find("ffprobe");
    private static string? Find(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? name + ".exe" : name)).FirstOrDefault(File.Exists);
}
