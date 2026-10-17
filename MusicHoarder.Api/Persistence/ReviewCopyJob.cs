namespace MusicHoarder.Api.Persistence;

/// <summary>Durable outbox: inserted in the same transaction as the plan metadata.
/// State is descriptive, never substituted for a content proof.</summary>
public sealed class ReviewCopyJob
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid PlanId { get; set; }
    public Guid RequestId { get; set; }
    public string Revision { get; set; } = "";
    public string State { get; set; } = "pending";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? RolledBackAtUtc { get; set; }
    public List<ReviewCopyTrack> Tracks { get; set; } = [];
}

public sealed class ReviewCopyTrack
{
    public Guid JobId { get; set; }
    public int SongId { get; set; }
    public string State { get; set; } = "pending";
    public int Attempts { get; set; }
    public string? FailureStage { get; set; }
    public string? FailureCode { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string StagingPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string? OldDestinationPath { get; set; }
    public string? OldPreviousPath { get; set; }
    public string BeforeJson { get; set; } = "";
    public string AfterJson { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string? DestinationBeforeSha256 { get; set; }
    public string? CandidateSha256 { get; set; }
    public string? ProofJson { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
}

public sealed class ReviewAudioCacheEntry
{
    public string Version { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string ProofJson { get; set; } = "";
}

/// <summary>All acknowledged request keys, including aliases for an existing plan job.</summary>
public sealed class ReviewCopyRequestKey
{
    public Guid OwnerUserId { get; set; }
    public Guid RequestId { get; set; }
    public Guid JobId { get; set; }
    public Guid PlanId { get; set; }
    public string Revision { get; set; } = "";
}
