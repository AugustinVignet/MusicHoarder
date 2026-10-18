using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Review.Retry;

internal static class PipelineStageJournal
{
    internal static async Task<PipelineStageState> RecordAsync(MusicHoarderDbContext db, SongMetadata song, string stage,
        string state, string? code, string? hash, string? candidate, Guid? jobId, int? exitCode,
        DateTime? retryAfter, bool sourceWarning, CancellationToken ct, bool resolveSourceWarning = false)
    {
        var current = await db.PipelineStageStates.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.SongId == song.Id && s.Stage == stage, ct);
        if (current is null)
        {
            current = new() { SongId = song.Id, OwnerUserId = song.OwnerUserId, Stage = stage };
            db.PipelineStageStates.Add(current);
        }
        current.State = state; current.Code = code; current.Attempts++; current.UpdatedAtUtc = DateTime.UtcNow;
        current.NextRetryAfterUtc = retryAfter; current.InputSha256 = hash; current.CandidateJson = candidate;
        current.Targeted |= jobId.HasValue;
        // Identification, provider success and a restart never erase an observed source warning.
        if (sourceWarning) { current.SourceWarningCode = code; current.SourceWarningInputSha256 = hash; }
        // Only an explicitly authorized targeted qualification with fresh complete
        // audio evidence resolves the current warning. Historical attempts remain.
        else if (resolveSourceWarning && jobId.HasValue && stage == "fingerprint" && state == "success")
        { current.SourceWarningCode = null; current.SourceWarningInputSha256 = null; }
        db.PipelineStageAttempts.Add(new() { Id = Guid.NewGuid(), SongId = song.Id, OwnerUserId = song.OwnerUserId,
            JobId = jobId, Stage = stage, State = state, Code = code, InputSha256 = hash, CandidateJson = candidate,
            ExitCode = exitCode, AttemptedAtUtc = current.UpdatedAtUtc, NextRetryAfterUtc = retryAfter });
        await db.SaveChangesAsync(ct);
        return current;
    }
}
internal sealed record FingerprintStageResult(string State, string? Code, string? Hash, FpcalcResult? Accepted);
internal static class FingerprintStage
{
    internal static bool FreshAccepted(PipelineStageState? state, SongMetadata song, string? hash)
    {
        if (state is not { State: "success", SourceWarningCode: null } || hash is null || state.InputSha256 != hash || state.CandidateJson is null) return false;
        try
        {
            using var json = JsonDocument.Parse(state.CandidateJson);
            var root = json.RootElement;
            var accepted = root.GetProperty("result").Deserialize<FpcalcResult>(ReviewCopyJobs.Json);
            return root.GetProperty("validationVersion").GetString() == FpcalcService.ValidationVersion
                && root.GetProperty("sourceAudio").GetProperty("verified").GetBoolean()
                && root.GetProperty("sourceAudio").GetProperty("source").GetProperty("contentSha256").GetString() == hash
                && root.GetProperty("sourceAudio").GetProperty("source").GetProperty("version").GetString()!.StartsWith(ReviewAudioVerifier.CodeVersion + ":", StringComparison.Ordinal)
                && accepted is not null && accepted.Fingerprint == song.Fingerprint && accepted.DurationSeconds == song.DurationSeconds;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    internal static async Task<FingerprintStageResult> ExecuteAsync(MusicHoarderDbContext db, SongMetadata song,
        string? path, IFileSystem files, IFpcalcService fpcalc, IReviewAudioCommandRunner tools,
        Guid? jobId, CancellationToken ct, bool resolveSourceWarnings = false)
    {
        string? before = null; FpcalcOutcome outcome = default;
        string state = "failed"; string? code = "source_missing"; string? candidate = null; FpcalcResult? accepted = null;
        var warning = false;
        var resolved = false;
        int? measuredDurationMs = null;
        try
        {
            before = path is null ? null : ReviewCopyJobs.HashIfPresent(files, path);
            if (before is not null)
            {
                outcome = await fpcalc.GetFingerprintAsync(path!, ct: ct);
                state = outcome.State; code = outcome.Code ?? outcome.FailureReason;
                var calculated = outcome.Candidate ?? outcome.Result;
                if (calculated is not null)
                {
                    // Validate the injectable outcome too, rather than trusting a fake/provider wrapper.
                    var validated = FpcalcService.Validate(new(outcome.ExitCode ?? (outcome.Result is null ? 3 : 0),
                        JsonSerializer.Serialize(new { duration = calculated.DurationSeconds, fingerprint = calculated.Fingerprint }),
                        state == "qualificationRequired" ? "reported audio diagnostic" : ""));
                    state = validated.State; code = validated.Code;
                    if (validated.Candidate is not null)
                    {
                        var audio = await new ReviewAudioVerifier(tools, new ReviewAudioProofStore(db)).VerifyAsync(path!, path!, ct);
                        DecodedDurationResult? measured = null;
                        var duration = 0d;
                        try { using var tag = TagLib.File.Create(path!); duration = tag.Properties.Duration.TotalSeconds; }
                        catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or ArgumentException) { }
                        var corrected = calculated;
                        if (!audio.Verified)
                        {
                            state = duration < 1 ? "failed" : "qualificationRequired";
                            code = duration < 1 ? "no_usable_audio" : "source_audio:" + audio.FailureCode;
                        }
                        else if (duration < 1 || Math.Abs(duration - calculated.DurationSeconds) > Math.Max(2, duration * .01))
                        {
                            if (audio.Source!.Streams.Count == 1)
                                measured = await tools.MeasureDurationAsync(path!, audio.Source.Streams[0], ct);
                            if (measured is { FailureCode: null, Seconds: >= 1 and <= 604800, SampleFrames: > 0 }
                                && Math.Abs(measured.Seconds.Value - measured.SampleFrames / (double)audio.Source.Streams[0].SampleRate) < 1e-9
                                && measured.AudioSha256 == audio.Source.DecodedAudioSha256)
                            {
                                corrected = calculated with { DurationSeconds = (int)Math.Round(measured.Seconds.Value, MidpointRounding.AwayFromZero) };
                                measuredDurationMs = (int)Math.Round(measured.Seconds.Value * 1000, MidpointRounding.AwayFromZero);
                                // A clean decode cannot distinguish a bad duration header
                                // from music cut at a valid frame boundary. Ordinary imports
                                // retain their fence; only targeted work can accept the
                                // measurement, and its automation hold still stays in place.
                                if (!jobId.HasValue)
                                { state = "qualificationRequired"; code = "fingerprint_duration_disagrees_with_source"; }
                            }
                            else { state = "qualificationRequired"; code = "fingerprint_duration_disagrees_with_source"; }
                        }
                        var previous = resolveSourceWarnings && jobId.HasValue && state == "success"
                            ? await db.PipelineStageStates.IgnoreQueryFilters().AsNoTracking()
                                .SingleOrDefaultAsync(s => s.SongId == song.Id && s.Stage == "fingerprint", ct) : null;
                        resolved = previous?.SourceWarningCode is not null;
                        candidate = JsonSerializer.Serialize(new { validationVersion = FpcalcService.ValidationVersion,
                            result = corrected, fpcalcExitCode = outcome.ExitCode, sourceAudio = audio,
                            durationEvidence = measured is null ? null : new { method = "decoded-pcm-samples-v1",
                                tagSeconds = duration, fpcalcSeconds = calculated.DurationSeconds, measurement = measured },
                            resolvedSourceWarning = resolved ? new { code = previous!.SourceWarningCode,
                                inputSha256 = previous.SourceWarningInputSha256, previousAttemptCount = previous.Attempts } : null }, ReviewCopyJobs.Json);
                        warning = state == "qualificationRequired";
                        if (state == "success") accepted = corrected;
                    }
                }
                if (ReviewCopyJobs.HashIfPresent(files, path!) != before)
                { state = "failed"; code = "source_changed_during_fingerprint"; accepted = null; warning = false; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { state = "failed"; code = "source_unreadable"; accepted = null; }
        if (accepted is not null)
        {
            song.Fingerprint = accepted.Fingerprint; song.DurationSeconds = accepted.DurationSeconds;
            if (measuredDurationMs.HasValue) song.DurationMs = measuredDurationMs;
        }
        await PipelineStageJournal.RecordAsync(db, song, "fingerprint", state, code, before, candidate,
            jobId, outcome.ExitCode, null, warning, ct, resolved && accepted is not null);
        return new(state, code, before, accepted);
    }
}
