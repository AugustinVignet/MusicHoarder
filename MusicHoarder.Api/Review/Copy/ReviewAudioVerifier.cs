using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Review.Copy;

internal sealed class ReviewAudioMetrics { internal long BytesHashed; internal int DecodeAttempts; }

// Optional private diagnostics: never included in the public result or failure code.
internal sealed record ReviewAudioSnapshotObservation(string Path, string Check,
    long BeforeSize, long AfterSize, long BeforeModifiedUtcTicks, long AfterModifiedUtcTicks,
    string? BeforeHash, string? AfterHash);

// Failure-only diagnostics contain no path or hash and do not enter receipt JSON.
internal sealed record ReviewAudioSnapshotFailure(string Check, long BeforeSize, long AfterSize,
    long BeforeModifiedUtcTicks, long AfterModifiedUtcTicks, bool? HashChanged);

/// <summary>Waits for a freshly written candidate's size and modification time to stop moving
/// before its first snapshot. Network mounts can publish the server-side write time about one
/// attribute-cache period after the writer closed the file; that one-off update must happen
/// before the guarded inspection, not during it. Content changes are still refused.</summary>
internal sealed record ReviewAudioSettle(TimeSpan Window, TimeSpan Poll, TimeSpan MaxWait)
{
    internal static readonly ReviewAudioSettle FreshCopy = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(15));
}

internal sealed record ReviewAudioStream(string Codec, int SampleRate, int Channels);
/// <summary>DecodeErrorsSha256 is null for a file that decodes cleanly under the strict check.
/// Otherwise it fingerprints the bounded, address-normalized diagnostics of a tolerant decode.
/// The diagnostic text is evidence only: a retag can move the parser's resync point, so the
/// copy is accepted on bit-identical encoded payload and decoded PCM, with damage on both
/// sides or on neither.</summary>
internal sealed record ReviewAudioProof(string Version, string ContentSha256, long SizeBytes,
    string EncodedAudioSha256, string DecodedAudioSha256, IReadOnlyList<ReviewAudioStream> Streams, DateTime VerifiedAtUtc,
    string? DecodeErrorsSha256 = null);
internal sealed record ReviewAudioResult(bool Verified, string? FailedStage, string? FailureCode,
    ReviewAudioProof? Source, ReviewAudioProof? Candidate, bool SourceCacheHit, bool CandidateCacheHit)
{
    // Observations, not proofs. Keep them internal so historical C receipt JSON stays unchanged.
    internal string? SourceReadSha256 { get; init; }
    internal string? CandidateReadSha256 { get; init; }
    internal ReviewAudioSnapshotFailure? SnapshotFailure { get; init; }
}

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
internal sealed partial class ReviewAudioVerifier(IReviewAudioCommandRunner runner, IReviewAudioProofStore? store = null,
    ReviewAudioMetrics? metrics = null, Action<ReviewAudioSnapshotObservation>? snapshotObserver = null,
    ReviewAudioSettle? candidateSettle = null)
{
    internal const string CodeVersion = "review-audio-v2-id3-boundaries";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    internal async Task<ReviewAudioResult> VerifyAsync(string source, string candidate, CancellationToken ct = default)
    {
        ReviewAudioProof? left = null;
        var leftHit = false;
        string? sourceRead = null, candidateRead = null;
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
            (left, leftHit) = await InspectAsync(source, key, ct, hash => sourceRead = hash);
            stage = "candidate";
            if (candidateSettle is not null) await SettleAsync(candidate, candidateSettle, ct);
            var (right, rightHit) = await InspectAsync(candidate, key, ct, hash => candidateRead = hash);
            var failure = !left.Streams.SequenceEqual(right.Streams) ? "stream_layout_changed"
                : left.EncodedAudioSha256 != right.EncodedAudioSha256 ? "encoded_audio_changed"
                : left.DecodedAudioSha256 != right.DecodedAudioSha256 ? "decoded_audio_changed"
                : (left.DecodeErrorsSha256 is null) != (right.DecodeErrorsSha256 is null) ? "decode_errors_changed" : null;
            if (failure is null)
            {
                stage = "comparison";
                var sourceNow = await SnapshotAsync(source, ct);
                var candidateNow = await SnapshotAsync(candidate, ct);
                if (sourceNow.Hash != left.ContentSha256 || candidateNow.Hash != right.ContentSha256)
                    throw new AudioCheckException("file_changed_after_verification");
            }
            return new(failure is null, failure is null ? null : "comparison", failure, left, right, leftHit, rightHit) { SourceReadSha256 = sourceRead, CandidateReadSha256 = candidateRead };
        }
        catch (AudioCheckException e) { return new(false, stage, e.Code, left, null, leftHit, false) { SourceReadSha256 = sourceRead, CandidateReadSha256 = candidateRead, SnapshotFailure = e.SnapshotFailure }; }
        catch (FileNotFoundException) { return new(false, stage, "file_missing", left, null, leftHit, false) { SourceReadSha256 = sourceRead, CandidateReadSha256 = candidateRead }; }
        catch (DirectoryNotFoundException) { return new(false, stage, "file_missing", left, null, leftHit, false) { SourceReadSha256 = sourceRead, CandidateReadSha256 = candidateRead }; }
        catch (UnauthorizedAccessException) { return new(false, stage, "file_unreadable", left, null, leftHit, false) { SourceReadSha256 = sourceRead, CandidateReadSha256 = candidateRead }; }
        catch (IOException) { return new(false, stage, "file_io_error", left, null, leftHit, false) { SourceReadSha256 = sourceRead, CandidateReadSha256 = candidateRead }; }
    }

    private async Task<(ReviewAudioProof Proof, bool CacheHit)> InspectAsync(string path, string version, CancellationToken ct, Action<string>? observed = null)
    {
        var before = await SnapshotAsync(path, ct);
        observed?.Invoke(before.Hash);
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
        CheckCommand(probe with { Errors = WithoutTagDiagnostics(probe.Errors) });
        var streams = ReadStreams(probe.Output);
        var common = new[] { "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-err_detect", "explode", "-i", path, "-map", "0:a" };
        var tolerant = new[] { "-nostdin", "-hide_banner", "-v", "error", "-i", path, "-map", "0:a" };
        var diagnostics = new List<string>();
        var encoded = await RunCheckedAsync(common, tolerant, ["-c:a", "copy", "-f", "streamhash", "-hash", "sha256", "-"], diagnostics, ct);
        var encodedHash = ReadHashes(encoded.Output, streams.Count);
        // Keep the strict demux check above, but exclude only physical boundary ID3 tags
        // for a single MP3 stream. Interior markers and unrecognized trailers stay hashed.
        if (streams is [{ Codec: "mp3" }] && Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            encodedHash = await ReviewMp3Payload.HashAsync(path, ct);
        // Decode the ENTIRE file, with no resampling/channel folding or normalization.
        // This catches invalid/truncated frames even when the demuxed payload can be hashed.
        if (metrics is not null) metrics.DecodeAttempts++;
        var decoded = await RunCheckedAsync(common, tolerant, ["-c:a", "pcm_s32le", "-f", "streamhash", "-hash", "sha256", "-"], diagnostics, ct);
        var decodedHash = ReadHashes(decoded.Output, streams.Count);
        var after = await SnapshotAsync(path, ct);
        snapshotObserver?.Invoke(new(path, "audio_inspection", before.Size, after.Size,
            before.Modified.Ticks, after.Modified.Ticks, before.Hash, after.Hash));
        if (before != after) throw new AudioCheckException("file_changed_during_verification",
            new("audio_inspection", before.Size, after.Size, before.Modified.Ticks, after.Modified.Ticks, before.Hash != after.Hash));
        var proof = new ReviewAudioProof(version, before.Hash, before.Size, encodedHash, decodedHash, streams, DateTime.UtcNow,
            diagnostics.Count == 0 ? null : Hash(string.Join('\n', diagnostics)));
        if (store is not null) await store.SaveAsync(proof, ct);
        return (proof, false);
    }

    /// <summary>Returns once size and write time were unchanged for the whole window, or after
    /// MaxWait. Never accepts a change: the strict snapshots that follow still decide.</summary>
    internal async Task<bool> SettleAsync(string path, ReviewAudioSettle settle, CancellationToken ct)
    {
        static (long Size, DateTime Modified) Stat(string p) { var i = new FileInfo(p); return (i.Length, i.LastWriteTimeUtc); }
        var first = Stat(path); var last = first;
        var total = Stopwatch.StartNew(); var stable = Stopwatch.StartNew();
        while (stable.Elapsed < settle.Window)
        {
            if (total.Elapsed >= settle.MaxWait) break;
            await Task.Delay(settle.Poll, ct);
            var now = Stat(path);
            if (now != last) { last = now; stable.Restart(); }
        }
        if (last != first)
            snapshotObserver?.Invoke(new(path, "candidate_settle", first.Size, last.Size, first.Modified.Ticks, last.Modified.Ticks, null, null));
        return stable.Elapsed >= settle.Window;
    }

    internal async Task<(string Hash, long Size, DateTime Modified)> SnapshotAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var length = info.Length; var modified = info.LastWriteTimeUtc;
        if (length == 0) throw new AudioCheckException("empty_file");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            digest.AppendData(buffer.AsSpan(0, read));
            if (metrics is not null) metrics.BytesHashed += read;
        }
        var hash = Convert.ToHexStringLower(digest.GetHashAndReset());
        info.Refresh();
        snapshotObserver?.Invoke(new(path, "snapshot_hash_read", length, info.Length,
            modified.Ticks, info.LastWriteTimeUtc.Ticks, null, hash));
        if (length != info.Length || modified != info.LastWriteTimeUtc) throw new AudioCheckException("file_changed_during_verification",
            new("snapshot_hash_read", length, info.Length, modified.Ticks, info.LastWriteTimeUtc.Ticks, null));
        return (hash, length, modified);
    }

    internal const int MaxToleratedDecodeErrorLines = 64;

    /// <summary>Runs the strict check. Only when the file itself reports decode errors (a damaged
    /// frame in an otherwise playable source) does it repeat the step without -xerror/explode and
    /// keep the normalized diagnostics. Tool failures, timeouts and truncated output stay refusals,
    /// as does a tolerant run that fails or reports more than a bounded number of error lines.</summary>
    private async Task<AudioCommandResult> RunCheckedAsync(string[] strict, string[] tolerant, string[] output,
        List<string> diagnostics, CancellationToken ct)
    {
        var result = await runner.RunAsync(false, [.. strict, .. output], ct);
        result = result with { Errors = WithoutTagDiagnostics(result.Errors) };
        if (result.Failure is not null || result.Truncated || (result.ExitCode == 0 && string.IsNullOrWhiteSpace(result.Errors)))
        {
            CheckCommand(result);
            return result;
        }
        var relaxed = await runner.RunAsync(false, [.. tolerant, .. output], ct);
        relaxed = relaxed with { Errors = WithoutTagDiagnostics(relaxed.Errors) };
        if (relaxed.Failure is not null) throw new AudioCheckException(relaxed.Failure);
        if (relaxed.Truncated) throw new AudioCheckException("tool_output_limit");
        if (relaxed.ExitCode != 0) throw new AudioCheckException("audio_tool_failed");
        var lines = NormalizeDiagnostics(string.IsNullOrWhiteSpace(relaxed.Errors) ? result.Errors : relaxed.Errors);
        if (lines.Count == 0 || lines.Count > MaxToleratedDecodeErrorLines) throw new AudioCheckException("audio_errors_reported");
        diagnostics.AddRange(lines);
        return relaxed;
    }

    /// <summary>ID3 parsing notices about the tag block (a mis-encoded comment frame, common in files
    /// re-tagged by download sites). They concern metadata, not audio frames, so they neither
    /// fail a check nor count as decode damage. Anything else on stderr still does.</summary>
    private static readonly HashSet<string> TagOnlyDiagnostics = new(StringComparer.Ordinal)
    {
        "Incorrect BOM value",
        "Error reading comment frame, skipped",
    };

    internal static string WithoutTagDiagnostics(string errors) =>
        string.Join('\n', errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !TagOnlyDiagnostics.Contains(ToolPrefix().Replace(line, ""))));

    [GeneratedRegex(@"^\[[^\]]+ @ 0x[0-9a-fA-F]+\]\s*")]
    private static partial Regex ToolPrefix();

    /// <summary>Removes per-process object addresses so identical damage yields identical text.</summary>
    internal static List<string> NormalizeDiagnostics(string errors) =>
        errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => ProcessAddress().Replace(line, "@"))
            .Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length > 0).ToList();

    [GeneratedRegex(@"@ 0x[0-9a-fA-F]+")]
    private static partial Regex ProcessAddress();

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
    internal sealed class AudioCheckException(string code, ReviewAudioSnapshotFailure? snapshotFailure = null) : Exception(code)
    {
        internal string Code { get; } = code;
        internal ReviewAudioSnapshotFailure? SnapshotFailure { get; } = snapshotFailure;
    }
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
