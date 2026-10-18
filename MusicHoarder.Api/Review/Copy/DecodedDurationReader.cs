using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MusicHoarder.Api.Review.Copy;

internal sealed record DecodedDurationResult(double? Seconds, long SampleFrames, string? AudioSha256, string? FailureCode);

/// <summary>Count actual decoded PCM bytes, independent of container timestamps,
/// bitrate/header estimates and fpcalc duration. No audio output file is created.</summary>
internal sealed class DecodedDurationReader(string executable, TimeSpan? commandTimeout = null)
{
    internal async Task<DecodedDurationResult> ReadAsync(string path, ReviewAudioStream stream, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (stream.SampleRate <= 0 || stream.Channels is < 1 or > 32)
            return new(null, 0, null, "invalid_audio_streams");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(executable) ? "ffmpeg" : executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-err_detect", "explode",
            "-i", path, "-map", "0:a:0", "-c:a", "pcm_s32le", "-f", "s32le", "-" }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception) { return new(null, 0, null, "audio_tool_unavailable"); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(commandTimeout ?? TimeSpan.FromMinutes(5));
        var output = CountAsync(process.StandardOutput.BaseStream, timeout.Token);
        var errors = ErrorsAsync(process.StandardError, timeout.Token);
        try
        {
            // Observe process cancellation before awaiting its pipes: a blocked
            // pipe reader must not delay killing a stalled decoder and its child.
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
            if (process.ExitCode != 0) return new(null, 0, null, "audio_tool_failed");
            var diagnostic = await errors;
            if (diagnostic.Truncated) return new(null, 0, null, "tool_output_limit");
            var mp3 = stream.Codec == "mp3" && Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(diagnostic.Text) && !(mp3 && ReviewAudioVerifier.KnownId3CommentDiagnostics(diagnostic.Text)))
                return new(null, 0, null, "audio_errors_reported");
            var pcm = await output;
            var width = stream.Channels * sizeof(int);
            if (pcm.Bytes == 0 || pcm.Bytes % width != 0) return new(null, 0, null, "invalid_decoded_sample_count");
            var frames = pcm.Bytes / width;
            var seconds = frames / (double)stream.SampleRate;
            if (seconds < 1 || seconds > 604800) return new(null, 0, null, "invalid_decoded_duration");
            // Same canonical one-stream aggregate as the fidelity verifier.
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pcm.Hash)));
            return new(seconds, frames, hash, null);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            try { await Task.WhenAll(output, errors); } catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
            ct.ThrowIfCancellationRequested();
            return new(null, 0, null, "audio_tool_timeout");
        }
        finally
        {
            // Also stop the child if a pipe/read error escapes before normal exit.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        }
    }
    private static async Task<(long Bytes, string Hash)> CountAsync(Stream input, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        { total = checked(total + read); hash.AppendData(buffer, 0, read); }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
    private static async Task<(string Text, bool Truncated)> ErrorsAsync(StreamReader input, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; var truncated = false; int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var keep = Math.Min(read, 32768 - text.Length);
            text.Append(buffer, 0, keep); truncated |= keep < read;
        }
        return (text.ToString(), truncated);
    }
}
