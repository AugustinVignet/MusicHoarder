namespace MusicHoarder.Api.Persistence;

public sealed class TargetedRetryJob
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid RequestId { get; set; }
    public string RequestJson { get; set; } = "";
    public string State { get; set; } = "pending";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<TargetedRetryItem> Items { get; set; } = [];
}
public sealed class TargetedRetryItem
{
    public Guid JobId { get; set; }
    public int SongId { get; set; }
    public int Ordinal { get; set; }
    public string Stage { get; set; } = "";
    public string State { get; set; } = "pending";
    public string? Code { get; set; }
    public string? InputSha256 { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
/// <summary>Current stage observation; attempts remain append-only history below.</summary>
public sealed class PipelineStageState
{
    public int SongId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Stage { get; set; } = "";
    public string State { get; set; } = "";
    public string? Code { get; set; }
    public int Attempts { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? NextRetryAfterUtc { get; set; }
    public string? InputSha256 { get; set; }
    public string? CandidateJson { get; set; }
    public string? SourceWarningCode { get; set; }
    public string? SourceWarningInputSha256 { get; set; }
    public bool Targeted { get; set; }
}
public sealed class PipelineStageAttempt
{
    public Guid Id { get; set; }
    public int SongId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? JobId { get; set; }
    public string Stage { get; set; } = "";
    public string State { get; set; } = "";
    public string? Code { get; set; }
    public int? ExitCode { get; set; }
    public string? InputSha256 { get; set; }
    public string? CandidateJson { get; set; }
    public DateTime AttemptedAtUtc { get; set; }
    public DateTime? NextRetryAfterUtc { get; set; }
}
