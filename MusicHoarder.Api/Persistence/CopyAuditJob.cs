namespace MusicHoarder.Api.Persistence;

/// <summary>Technical read-only work; no Song FK so historical receipts survive removals.</summary>
public sealed class CopyAuditJob
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public Guid? ParentJobId { get; set; }
    public string ScopeJson { get; set; } = "";
    public string ExpectedContextRevision { get; set; } = "";
    public string? ObservedContextRevision { get; set; }
    public string ScopeState { get; set; } = "notReobserved";
    public string State { get; set; } = "queued";
    public bool CancelRequested { get; set; }
    public string? WaitReason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public List<CopyAuditTrack> Tracks { get; set; } = [];
}
public sealed class CopyAuditTrack
{
    public Guid JobId { get; set; }
    public int SongId { get; set; }
    public string ExpectedDbRevision { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string? DestinationPath { get; set; }
    public string State { get; set; } = "queued";
    public int Attempts { get; set; }
    public int TransientFailures { get; set; }
    public Guid? ClaimId { get; set; }
    public string? FailureCode { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? ReceiptJson { get; set; }
}
public sealed class CopyAuditAttempt
{
    public Guid JobId { get; set; }
    public int SongId { get; set; }
    public int Number { get; set; }
    public Guid ClaimId { get; set; }
    public string State { get; set; } = "running";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public long BytesHashed { get; set; }
    public int DecodeAttempts { get; set; }
    public int CacheHits { get; set; }
    public string? ReceiptJson { get; set; }
}
