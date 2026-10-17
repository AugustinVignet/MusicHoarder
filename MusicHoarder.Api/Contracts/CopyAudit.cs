using System.Text.Json.Serialization;

namespace MusicHoarder.Api.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CopyAuditRequest(Guid RequestId, AlbumReviewContextRequest Scope, string ContextRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CopyAuditRetryRequest(Guid RequestId, int[] SongIds);
public sealed record CopyAuditStage(string Stage, string State, string? Code = null, IReadOnlyList<string>? Fields = null);
public sealed record CopyAuditAudioStream(string Codec, int SampleRate, int Channels);
public sealed record CopyAuditContentProof(string Version, string ContentSha256, long SizeBytes,
    string EncodedAudioSha256, string DecodedAudioSha256, IReadOnlyList<CopyAuditAudioStream> Streams, DateTime VerifiedAtUtc);
public sealed record CopyAuditReceipt(int SongId, int Attempt, string State, string? FailureCode,
    string ExpectedDbRevision, string? ObservedDbRevision, string ExpectedContextRevision, string? ObservedContextRevision,
    DateTime StartedAtUtc, DateTime FinishedAtUtc, string VerifierVersion,
    AlbumReviewFileObservation Source, AlbumReviewFileObservation Copy, IReadOnlyList<CopyAuditStage> Stages,
    string? SourceReadSha256, string? CopyReadSha256, CopyAuditContentProof? SourceProof, CopyAuditContentProof? CopyProof,
    bool SourceCacheHit, bool CopyCacheHit, long BytesHashed, int DecodeAttempts, DateTime? ContentObservedAtUtc, bool ContentVerified);
public sealed record CopyAuditTrackResult(int SongId, string State, int Attempts, string? FailureCode,
    DateTime UpdatedAtUtc, DateTime? NextAttemptAtUtc, CopyAuditReceipt? Receipt);
public sealed record CopyAuditJobResult(Guid Id, Guid RequestId, Guid? ParentJobId, AlbumReviewContextRequest Scope, IReadOnlyList<int> AuditedSongIds, string State,
    string ExpectedContextRevision, string? ObservedContextRevision, string ScopeState,
    DateTime CreatedAtUtc, DateTime UpdatedAtUtc, DateTime ObservedAtUtc, DateTime? NextAttemptAtUtc,
    string? WaitReason, bool CancelRequested, int Total, IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<CopyAuditTrackResult> Results, int? NextAfterSongId, string Pagination,
    long BytesHashed, int DecodeAttempts, int CacheHits, int ActiveAttempts, int QueuedJobs, string MetricsCoverage);
public sealed record CopyAuditAttemptResult(int Number, string State, DateTime StartedAtUtc,
    DateTime? FinishedAtUtc, CopyAuditReceipt? Receipt);
public sealed record CopyAuditAttemptsPage(int SongId, IReadOnlyList<CopyAuditAttemptResult> Attempts, int? NextAfterAttempt);
