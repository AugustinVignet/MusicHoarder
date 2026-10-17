using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Review;

public sealed class FpcalcValidationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mh-fpcalc-" + Guid.NewGuid().ToString("N"));
    internal static string Fingerprint => Convert.ToBase64String(new byte[] { 1, 0, 0, 20, 0, 0, 0, 0, 0, 0, 0, 0 }).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string ValidJson => JsonSerializer.Serialize(new { duration = 12.0, fingerprint = Fingerprint });
    public FpcalcValidationTests() => Directory.CreateDirectory(root);
    [Theory]
    [InlineData("empty")]
    [InlineData("json")]
    [InlineData("array")]
    [InlineData("missingDuration")]
    [InlineData("zeroDuration")]
    [InlineData("negativeDuration")]
    [InlineData("shortDuration")]
    [InlineData("hugeDuration")]
    [InlineData("stringDuration")]
    [InlineData("missingFingerprint")]
    [InlineData("emptyFingerprint")]
    [InlineData("invalidFingerprint")]
    [InlineData("duplicateProperty")]
    [InlineData("truncatedOutput")]
    [InlineData("timeout")]
    [InlineData("missingTool")]
    public void InvalidOutputsNeverProduceUsableFingerprints(string kind)
    {
        var data = kind switch
        {
            "empty" => "", "json" => "{", "array" => "[]", "missingDuration" => "{\"fingerprint\":\"x\"}",
            "missingFingerprint" => "{\"duration\":12}", "emptyFingerprint" => "{\"duration\":12,\"fingerprint\":\"\"}",
            "invalidFingerprint" => "{\"duration\":12,\"fingerprint\":\"not-a-chromaprint\"}",
            "duplicateProperty" => "{\"duration\":12,\"duration\":13,\"fingerprint\":\"x\"}",
            _ => ValidJson.Replace("12", kind switch { "zeroDuration" => "0", "negativeDuration" => "-1", "shortDuration" => "0.5",
                "hugeDuration" => "604801", "stringDuration" => "\"12\"", _ => "12" })
        };
        var outcome = FpcalcService.Validate(new(0, data, "", kind == "timeout" ? "audio_tool_timeout" : kind == "missingTool" ? "audio_tool_unavailable" : null,
            kind == "truncatedOutput"));
        Assert.Equal("failed", outcome.State); Assert.Null(outcome.Result); Assert.NotNull(outcome.Code);
    }
    [Theory]
    [InlineData(3, "")]
    [InlineData(1, "broken frame")]
    [InlineData(0, "decoder warning")]
    public void ValidJsonWithNonzeroExitOrDiagnostics_RequiresQualification(int code, string errors)
    {
        var result = FpcalcService.Validate(new(code, ValidJson, errors));
        Assert.Equal("qualificationRequired", result.State); Assert.Null(result.Result); Assert.NotNull(result.Candidate);
        Assert.Equal(code, result.ExitCode);
    }
    [Fact]
    public void CleanValidatedOutputIsUsable_AndDoesNotAcceptArbitraryNonemptyStrings()
    {
        var good = FpcalcService.Validate(new(0, ValidJson, ""));
        Assert.Equal("success", good.State); Assert.Equal(Fingerprint, good.Result!.Fingerprint); Assert.Equal(12, good.Result.DurationSeconds);
        Assert.Null(FpcalcService.Validate(new(0, "{\"duration\":12,\"fingerprint\":\"garbage\"}", "")).Result);
    }
    [UnixFact]
    public async Task ActualProcess_NonzeroExitPreservesCandidateButNeverPublishesIt_ArgumentsAreNotShellCode()
    {
        var executable = Path.Combine(root, "fixture fpcalc");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s' '" + ValidJson + "'\nexit 3\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var service = new FpcalcService(Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { FpcalcPath = executable }), NullLogger<FpcalcService>.Instance);
        var result = await service.GetFingerprintAsync(Path.Combine(root, "input; touch UNEXPECTED"));
        Assert.Equal("qualificationRequired", result.State); Assert.NotNull(result.Candidate); Assert.Null(result.Result);
        Assert.False(File.Exists(Path.Combine(root, "UNEXPECTED")));
    }
    [UnixFact]
    public async Task ActualProcess_TimeoutAndCancellationKillTheChild()
    {
        var executable = Path.Combine(root, "slow-fpcalc");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nwhile :; do printf noisy; printf noisy >&2; done\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runner = new FpcalcCommandRunner(executable, TimeSpan.FromMilliseconds(120));
        var result = await runner.RunAsync("fixture", null, default); Assert.Equal("audio_tool_timeout", result.Failure);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FpcalcCommandRunner(executable, TimeSpan.FromSeconds(5)).RunAsync("fixture", null, cancel.Token));
    }
    [FpcalcFact]
    public async Task ActualFpcalc_AndFullAudioCheck_AcceptSyntheticFlac_RejectMetadataOnly()
    {
        var path = Path.Combine(root, "source.flac");
        var generated = await new ReviewAudioCommandRunner(AudioTools.Ffmpeg!).ExecuteAsync(false,
            ["-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=12", "-c:a", "flac", path], default);
        Assert.Equal(0, generated.ExitCode);
        var service = new FpcalcService(Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { FpcalcPath = FpcalcTools.Path! }), NullLogger<FpcalcService>.Instance);
        var good = await service.GetFingerprintAsync(path);
        Assert.Equal("success", good.State); Assert.NotNull(good.Result); Assert.Equal(12, good.Result.DurationSeconds);
        var empty = Path.Combine(root, "metadata-only.mp3"); await File.WriteAllBytesAsync(empty, [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0]);
        var bad = await service.GetFingerprintAsync(empty); Assert.Equal("failed", bad.State); Assert.Null(bad.Result);
    }
    [FpcalcFact]
    public async Task ActualTruncatedFlac_ReturnsJsonAndFingerprint_ButNeverAUsableSuccess()
    {
        var path = Path.Combine(root, "truncated.flac");
        var generated = await new ReviewAudioCommandRunner(AudioTools.Ffmpeg!).ExecuteAsync(false,
            ["-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=12", "-c:a", "flac", path], default);
        Assert.Equal(0, generated.ExitCode);
        using (var truncate = File.OpenWrite(path)) truncate.SetLength(truncate.Length - 100);
        var service = new FpcalcService(Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { FpcalcPath = FpcalcTools.Path! }), NullLogger<FpcalcService>.Instance);
        var result = await service.GetFingerprintAsync(path);
        Assert.Equal("qualificationRequired", result.State); Assert.NotNull(result.Candidate);
        Assert.Equal(3, result.ExitCode); Assert.Null(result.Result);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
internal static class FpcalcTools
{
    internal static string? Path => Find();
    private static string? Find()
    {
        var configured = Environment.GetEnvironmentVariable("MH_TEST_FPCALC");
        if (configured is not null && File.Exists(configured)) return configured;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
            .Select(p => System.IO.Path.Combine(p, OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc")).FirstOrDefault(File.Exists);
    }
}
public sealed class FpcalcFactAttribute : FactAttribute
{
    public FpcalcFactAttribute() { if (FpcalcTools.Path is null || AudioTools.Ffmpeg is null) Skip = "Requires fpcalc, ffmpeg and ffprobe for the real synthetic audio fixture."; }
}
