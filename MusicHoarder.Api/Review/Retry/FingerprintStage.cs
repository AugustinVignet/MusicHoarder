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
        DateTime? retryAfter, bool sourceWarning, CancellationToken ct)
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
        // A warning describes the bytes it was observed on. A later success on different source bytes
        // (the file was replaced or repaired) supersedes it; the attempt history still keeps it.
        else if (state == "success" && hash is not null && current.SourceWarningInputSha256 is not null && current.SourceWarningInputSha256 != hash)
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
        Guid? jobId, CancellationToken ct)
    {
        string? before = null; FpcalcOutcome outcome = default;
        string state = "failed"; string? code = "source_missing"; string? candidate = null; FpcalcResult? accepted = null;
        var warning = false;
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
                        var duration = 0d;
                        try { using var tag = TagLib.File.Create(path!); duration = tag.Properties.Duration.TotalSeconds; }
                        catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or ArgumentException) { }
                        if (duration < 1) { state = "failed"; code = "no_usable_audio"; }
                        else if (!audio.Verified) { state = "qualificationRequired"; code = "source_audio:" + audio.FailureCode; }
                        else if (Math.Abs(duration - calculated.DurationSeconds) > Math.Max(2, duration * .01))
                        { state = "qualificationRequired"; code = "fingerprint_duration_disagrees_with_source"; }
                        candidate = JsonSerializer.Serialize(new { validationVersion = FpcalcService.ValidationVersion,
                            result = calculated, fpcalcExitCode = outcome.ExitCode, sourceAudio = audio }, ReviewCopyJobs.Json);
                        warning = state == "qualificationRequired";
                        if (state == "success") accepted = calculated;
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
        }
        await PipelineStageJournal.RecordAsync(db, song, "fingerprint", state, code, before, candidate,
            jobId, outcome.ExitCode, null, warning, ct);
        return new(state, code, before, accepted);
    }
}
