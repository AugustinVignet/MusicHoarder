namespace MusicHoarder.Api.Contracts;

public sealed record TargetedRetryRequest(Guid RequestId, int[] SongIds, string[] Stages,
    bool AllowProviders = false, Guid? CopyJobId = null, bool RecomputeSuccessful = false,
    bool RespectRetryAfter = true);
public sealed record TargetedRetryItemResult(int SongId, string Stage, string State, string? Code,
    string? InputSha256, DateTime? StartedAtUtc, DateTime? CompletedAtUtc);
public sealed record TargetedRetryResult(Guid Id, Guid RequestId, string State, DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc, IReadOnlyList<TargetedRetryItemResult> Items);
public sealed record PipelineStageResult(int SongId, string Stage, string State, string? Code, int Attempts,
    DateTime UpdatedAtUtc, DateTime? NextRetryAfterUtc, string? InputSha256, string? CandidateJson,
    string? SourceWarningCode, string? SourceWarningInputSha256);
public sealed record PipelineAttemptResult(Guid Id, int SongId, Guid? JobId, string Stage, string State,
    string? Code, int? ExitCode, string? InputSha256, string? CandidateJson, DateTime AttemptedAtUtc, DateTime? NextRetryAfterUtc);
public sealed record PipelineFailuresPage(int Offset, int Limit, bool HasMore, IReadOnlyList<PipelineStageResult> Items);
public sealed record ResumeAutomationRequest(int[] SongIds, bool AllowProviders, bool AllowBuild, bool AllowDuplicateDetection);
