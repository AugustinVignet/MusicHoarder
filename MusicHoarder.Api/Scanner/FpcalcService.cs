using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Review.Copy;

namespace MusicHoarder.Api.Scanner;

public record FpcalcResult(int DurationSeconds, string Fingerprint);
public readonly record struct FpcalcOutcome(FpcalcResult? Result, string? FailureReason)
{
    public string State { get; init; } = Result is null ? "failed" : "success";
    public string? Code { get; init; }
    public int? ExitCode { get; init; }
    public FpcalcResult? Candidate { get; init; } = Result;
    public static FpcalcOutcome Success(FpcalcResult result) => new(result, null) { ExitCode = 0 };
    public static FpcalcOutcome Failure(string reason) => new(null, reason) { Code = "fingerprint_failed" };
}
public interface IFpcalcService
{
    Task<FpcalcOutcome> GetFingerprintAsync(string filePath, int? maxLengthSeconds = null, CancellationToken ct = default);
}
public sealed record FpcalcCommandResult(int? ExitCode, string Output, string Errors, string? Failure = null, bool Truncated = false);
public interface IFpcalcCommandRunner
{
    Task<FpcalcCommandResult> RunAsync(string path, int? lengthSeconds, CancellationToken ct);
}
internal sealed class FpcalcCommandRunner(string executable, TimeSpan? timeout = null) : IFpcalcCommandRunner
{
    public async Task<FpcalcCommandResult> RunAsync(string path, int? lengthSeconds, CancellationToken ct)
    {
        var args = new List<string> { "-json" };
        if (lengthSeconds is > 0) { args.Add("-length"); args.Add(lengthSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        args.Add(path);
        // Reuse the bounded audio-process launcher: no shell, concurrent capped pipes, kill on timeout/cancel.
        var result = await new ReviewAudioCommandRunner(string.IsNullOrWhiteSpace(executable) ? "fpcalc" : executable, timeout)
            .ExecuteAsync(false, args, ct);
        return new(result.ExitCode, result.Output, result.Errors, result.Failure, result.Truncated);
    }
}
public class FpcalcService(IOptions<MusicEnricherOptions> options, ILogger<FpcalcService> logger,
    IFpcalcCommandRunner? runner = null) : IFpcalcService
{
    internal const string ValidationVersion = "fpcalc-validation-v1";
    public async Task<FpcalcOutcome> GetFingerprintAsync(string filePath, int? maxLengthSeconds = null, CancellationToken ct = default)
    {
        try
        {
            var command = await (runner ?? new FpcalcCommandRunner(options.Value.FpcalcPath)).RunAsync(filePath, maxLengthSeconds, ct);
            return Validate(command);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            logger.LogDebug(e, "Fingerprint command failed");
            return FpcalcOutcome.Failure("fingerprint_command_failed") with { Code = "fingerprint_command_failed" };
        }
    }
    internal static FpcalcOutcome Validate(FpcalcCommandResult command)
    {
        FpcalcOutcome Fail(string code) => FpcalcOutcome.Failure(code) with { Code = code, ExitCode = command.ExitCode };
        if (command.Failure is not null) return Fail(command.Failure);
        if (command.Truncated || command.Output.Length > 32768 || command.Errors.Length > 32768) return Fail("fpcalc_output_limit");
        FpcalcResult candidate;
        try
        {
            using var document = JsonDocument.Parse(command.Output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1)) return Fail("invalid_fpcalc_json");
            if (!root.TryGetProperty("duration", out var duration) || !duration.TryGetDouble(out var seconds)
                || !double.IsFinite(seconds) || seconds < 1 || seconds > 604800) return Fail("invalid_fingerprint_duration");
            if (!root.TryGetProperty("fingerprint", out var fingerprint) || fingerprint.ValueKind != JsonValueKind.String) return Fail("invalid_fingerprint");
            var text = fingerprint.GetString();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 32000 || !ChromaprintComparer.TryDecode(text, out var frames) || frames.Length == 0)
                return Fail("invalid_fingerprint");
            candidate = new((int)seconds, text);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return Fail("invalid_fpcalc_json"); }
        if (command.ExitCode != 0 || !string.IsNullOrWhiteSpace(command.Errors))
            return new(null, "Audio output requires qualification") { State = "qualificationRequired",
                Code = command.ExitCode != 0 ? "fpcalc_nonzero_exit" : "fpcalc_audio_diagnostics", ExitCode = command.ExitCode, Candidate = candidate };
        return FpcalcOutcome.Success(candidate);
    }
}
