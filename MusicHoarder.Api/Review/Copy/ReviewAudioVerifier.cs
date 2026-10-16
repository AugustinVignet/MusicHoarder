using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Review.Copy;

internal sealed record ReviewAudioStream(string Codec, int SampleRate, int Channels);
internal sealed record ReviewAudioProof(string Version, string ContentSha256, long SizeBytes,
    string EncodedAudioSha256, string DecodedAudioSha256, IReadOnlyList<ReviewAudioStream> Streams, DateTime VerifiedAtUtc);
internal sealed record ReviewAudioResult(bool Verified, string? FailedStage, string? FailureCode,
    ReviewAudioProof? Source, ReviewAudioProof? Candidate, bool SourceCacheHit, bool CandidateCacheHit);

/// <summary>The durable job store supplies this cache. A content hash alone is never a
/// proof: the matching verifier/tool version and complete successful checks are required.</summary>
internal interface IReviewAudioProofStore
{
    Task<ReviewAudioProof?> FindAsync(string version, string contentSha256, CancellationToken ct);
    Task SaveAsync(ReviewAudioProof proof, CancellationToken ct);
}

internal sealed record AudioCommandResult(int? ExitCode, string Output, string Errors, string? Failure = null, bool Truncated = false);
internal interface IReviewAudioCommandRunner
{
    Task<AudioCommandResult> RunAsync(bool probe, IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>Read-only audio fidelity checks, independent of plan B's persistence/HTTP contract.
/// No process shell, provider, transcoded output file, musical approval or publication.</summary>
internal sealed partial class ReviewAudioVerifier(IReviewAudioCommandRunner runner, IReviewAudioProofStore? store = null)
{
    internal const string CodeVersion = "review-audio-v1";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    internal async Task<ReviewAudioResult> VerifyAsync(string source, string candidate, CancellationToken ct = default)
    {
        ReviewAudioProof? left = null;
        var leftHit = false;
        var stage = "tools";
        try
        {
            // Probe both binaries for each comparison: a changed tool invalidates cached proofs.
            var versions = new List<string>();
            foreach (var probe in new[] { false, true })
            {
                var version = await runner.RunAsync(probe, ["-version"], ct);
                CheckCommand(version);
                if (string.IsNullOrWhiteSpace(version.Output)) throw new AudioCheckException("invalid_tool_output");
                versions.Add(version.Output);
            }
            var key = CodeVersion + ":" + Hash(string.Join('\n', versions));
            stage = "source";
            (left, leftHit) = await InspectAsync(source, key, ct);
            stage = "candidate";
            var (right, rightHit) = await InspectAsync(candidate, key, ct);
            var failure = !left.Streams.SequenceEqual(right.Streams) ? "stream_layout_changed"
                : left.EncodedAudioSha256 != right.EncodedAudioSha256 ? "encoded_audio_changed"
                : left.DecodedAudioSha256 != right.DecodedAudioSha256 ? "decoded_audio_changed" : null;
            if (failure is null)
            {
                stage = "comparison";
                var sourceNow = await SnapshotAsync(source, ct);
                var candidateNow = await SnapshotAsync(candidate, ct);
                if (sourceNow.Hash != left.ContentSha256 || candidateNow.Hash != right.ContentSha256)
                    throw new AudioCheckException("file_changed_after_verification");
            }
            return new(failure is null, failure is null ? null : "comparison", failure, left, right, leftHit, rightHit);
        }
        catch (AudioCheckException e) { return new(false, stage, e.Code, left, null, leftHit, false); }
        catch (FileNotFoundException) { return new(false, stage, "file_missing", left, null, leftHit, false); }
        catch (DirectoryNotFoundException) { return new(false, stage, "file_missing", left, null, leftHit, false); }
        catch (UnauthorizedAccessException) { return new(false, stage, "file_unreadable", left, null, leftHit, false); }
        catch (IOException) { return new(false, stage, "file_io_error", left, null, leftHit, false); }
    }

    private async Task<(ReviewAudioProof Proof, bool CacheHit)> InspectAsync(string path, string version, CancellationToken ct)
    {
        var before = await SnapshotAsync(path, ct);
        if (store is not null)
        {
            var cached = await store.FindAsync(version, before.Hash, ct);
            if (cached is not null && cached.Version == version && cached.ContentSha256 == before.Hash
                && cached.SizeBytes == before.Size && IsHash(cached.EncodedAudioSha256) && IsHash(cached.DecodedAudioSha256)
                && cached.Streams is { Count: > 0 and <= 32 } && cached.Streams.All(s => s.SampleRate > 0 && s.Channels > 0 && !string.IsNullOrEmpty(s.Codec)))
                return (cached, true);
        }
        var probe = await runner.RunAsync(true,
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index,codec_name,sample_rate,channels", "-of", "json", path], ct);
        CheckCommand(probe);
        var streams = ReadStreams(probe.Output);
        var common = new[] { "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-err_detect", "explode", "-i", path, "-map", "0:a" };
        var encoded = await runner.RunAsync(false, [.. common, "-c:a", "copy", "-f", "streamhash", "-hash", "sha256", "-"], ct);
        CheckCommand(encoded);
        var encodedHash = ReadHashes(encoded.Output, streams.Count);
        // Decode the ENTIRE file, with no resampling/channel folding or normalization.
        // This catches invalid/truncated frames even when the demuxed payload can be hashed.
        var decoded = await runner.RunAsync(false, [.. common, "-c:a", "pcm_s32le", "-f", "streamhash", "-hash", "sha256", "-"], ct);
        CheckCommand(decoded);
        var decodedHash = ReadHashes(decoded.Output, streams.Count);
        var after = await SnapshotAsync(path, ct);
        if (before != after) throw new AudioCheckException("file_changed_during_verification");
        var proof = new ReviewAudioProof(version, before.Hash, before.Size, encodedHash, decodedHash, streams, DateTime.UtcNow);
        if (store is not null) await store.SaveAsync(proof, ct);
        return (proof, false);
    }

    private static async Task<(string Hash, long Size, DateTime Modified)> SnapshotAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var length = info.Length; var modified = info.LastWriteTimeUtc;
        if (length == 0) throw new AudioCheckException("empty_file");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
        info.Refresh();
        if (length != info.Length || modified != info.LastWriteTimeUtc) throw new AudioCheckException("file_changed_during_verification");
        return (hash, length, modified);
    }

    private static IReadOnlyList<ReviewAudioStream> ReadStreams(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.GetProperty("streams");
            if (rows.GetArrayLength() is < 1 or > 32) throw new AudioCheckException("invalid_audio_streams");
            var result = rows.EnumerateArray().OrderBy(s => s.GetProperty("index").GetInt32())
                .Select(s => new ReviewAudioStream(s.GetProperty("codec_name").GetString()!,
                    int.Parse(s.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture),
                    s.GetProperty("channels").GetInt32())).ToArray();
            if (result.Any(s => string.IsNullOrEmpty(s.Codec) || s.SampleRate <= 0 || s.Channels <= 0))
                throw new AudioCheckException("invalid_audio_streams");
            return result;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentNullException)
        { throw new AudioCheckException("invalid_tool_output"); }
    }

    private static string ReadHashes(string text, int count)
    {
        var values = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => StreamHash().Match(line)).ToArray();
        if (values.Length != count || values.Any(m => !m.Success || !IsHash(m.Groups[2].Value))
            || values.Where((m, i) => m.Groups[1].Value != i.ToString(CultureInfo.InvariantCulture)).Any())
            throw new AudioCheckException("invalid_audio_hash_output");
        return Hash(string.Join('\n', values.Select(m => m.Groups[2].Value.ToLowerInvariant())));
    }
    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit) && !value.Equals(EmptySha256, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void CheckCommand(AudioCommandResult result)
    {
        if (result.Failure is not null) throw new AudioCheckException(result.Failure);
        if (result.Truncated) throw new AudioCheckException("tool_output_limit");
        if (result.ExitCode != 0) throw new AudioCheckException("audio_tool_failed");
        if (!string.IsNullOrWhiteSpace(result.Errors)) throw new AudioCheckException("audio_errors_reported");
    }
    [GeneratedRegex(@"^(\d+),a,SHA256=([a-fA-F0-9]{64})$")]
    private static partial Regex StreamHash();
    private sealed class AudioCheckException(string code) : Exception(code) { internal string Code { get; } = code; }
}

/// <summary>Bounded process I/O; always drains both pipes, cancels/kills the whole process tree.
/// Failure codes never include private paths or unbounded ffmpeg diagnostics.</summary>
internal sealed class ReviewAudioCommandRunner(string ffmpegPath, TimeSpan? commandTimeout = null) : IReviewAudioCommandRunner
{
    internal async Task<AudioCommandResult> ExecuteAsync(bool probe, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ffmpeg = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
        var executable = probe ? ProbePath(ffmpeg) : ffmpeg;
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception) { return new(null, "", "", "audio_tool_unavailable"); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(commandTimeout ?? TimeSpan.FromMinutes(5));
        // Keep draining after the cap so a noisy process cannot fill the pipe and deadlock.
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
            ct.ThrowIfCancellationRequested();
            return new(null, "", "", "audio_tool_timeout");
        }
        var output = await stdout; var errors = await stderr;
        return new(process.ExitCode, output.Text, errors.Text, Truncated: output.Truncated || errors.Truncated);
    }
    Task<AudioCommandResult> IReviewAudioCommandRunner.RunAsync(bool probe, IReadOnlyList<string> arguments, CancellationToken ct)
        => ExecuteAsync(probe, arguments, ct);
    private static string ProbePath(string ffmpeg)
    {
        var name = Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
        return Path.GetDirectoryName(ffmpeg) is { Length: > 0 } directory ? Path.Combine(directory, name) : name;
    }
    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        const int limit = 32768;
        var text = new StringBuilder(); var buffer = new char[4096]; var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var keep = Math.Min(read, limit - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
            if (keep < read) truncated = true;
        }
        return (text.ToString(), truncated);
    }
}
