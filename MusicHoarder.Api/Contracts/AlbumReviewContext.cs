using System.Text.Json.Serialization;

namespace MusicHoarder.Api.Contracts;

/// <summary>Read-only selection. Froms are exact POSIX source parents, never prefixes.
/// Null TargetSongIds selects all their members; a supplied list selects an explicit subset.
/// Context neighbors never become selected targets or an established musical program.</summary>
public sealed record AlbumReviewContextRequest(string[] Froms, int[]? TargetSongIds = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AlbumAuditScope? Audit = null);

public sealed record AlbumReviewGroup(
    string From, int AnchorSongId, int TrackCount, int RemainingCount,
    int MatchedCount, string? Album, string? AlbumArtist, int AlbumNameCount);

public sealed record AlbumReviewGroupsPage(
    IReadOnlyList<AlbumReviewGroup> Groups, int Offset, int Limit, int? NextOffset,
    string Order, string RemainingDefinition);

public sealed record AlbumReviewScope(
    string SelectionKind, IReadOnlyList<string> Froms, IReadOnlyList<int> TargetSongIds,
    bool ProgramEstablished, string Completeness);

/// <summary>Values contains only captured fields for originals; null is a captured empty tag.
/// Fields absent from Values were never captured, and must not be inferred from current tags.</summary>
public sealed record AlbumReviewTags(
    string Provenance, DateTime? CapturedAtUtc, IReadOnlyDictionary<string, object?> Values);

public sealed record AlbumReviewChange(
    int Id, string Field, string? OldValue, string? NewValue, string Source,
    double Confidence, DateTime CreatedAtUtc, DateTime? AppliedAtUtc, DateTime? RevertedAtUtc,
    Guid? ActorUserId = null, string? Reason = null, string? EvidenceReferencesJson = null, string? ReviewPlanRevision = null);

public sealed record AlbumReviewManualField(string Field, string? Value, string State, int AuditId);

/// <summary>No raw provider JSON is exposed. Candidate metadata is a fixed allowlist.</summary>
public sealed record AlbumReviewCandidate(
    int AttemptId, string Provider, string Status, DateTime CollectedAtUtc,
    DateTime? RetryAfterUtc, bool HasError, string CandidateState,
    AlbumReviewTags? Tags, int? DurationSeconds, double? Confidence,
    IReadOnlyList<string> Warnings);

public sealed record AlbumReviewCopy(
    string? Path, string Presence, long? SizeBytes, DateTime? LastModifiedUtc,
    string BuildStatus, int BuildAttempts, DateTime? BuiltAtUtc,
    DateTime? LastAttemptedAtUtc, bool HasBuildError, string Verification,
    AlbumReviewFileObservation? Previous);

public sealed record AlbumReviewFileObservation(
    string Path, string Presence, long? SizeBytes, DateTime? LastModifiedUtc);

public sealed record AlbumReviewTrack(
    int Id, string From, string SourcePath, string FileName, IReadOnlyList<string> ContextReasons,
    long SourceSizeBytes, DateTime SourceModifiedAtUtc, string? MatchedBy, double? MatchConfidence,
    DateTime? EnrichmentLastAttemptedAtUtc,
    string EnrichmentStatus, DateTime IndexedAtUtc, DateTime? EnrichedAtUtc,
    int? DurationMs, int? DurationSeconds, AlbumReviewTags Original, AlbumReviewTags Current,
    bool IsManuallyApproved, DateTime? ManuallyApprovedAtUtc,
    IReadOnlyList<AlbumReviewManualField> ManualFields,
    IReadOnlyList<AlbumReviewChange> Changes, IReadOnlyList<AlbumReviewCandidate> Candidates,
    IReadOnlyList<string> Warnings, bool HasEnrichmentError, bool IsDuplicate, int? DuplicateOfId,
    AlbumReviewCopy Copy, Guid? ReviewPlanId = null, bool IsSynthetic = false, bool IsUnreleased = false);

public sealed record AlbumReviewDuplicate(
    int Id, int SongIdLow, int SongIdHigh, string Status, string Confidence,
    string Reasons, double? Similarity, DateTime DetectedAtUtc, DateTime? DismissedAtUtc);

public sealed record AlbumReviewConflict(string Kind, string Key, IReadOnlyList<int> SongIds);

public sealed record AlbumReviewProgramTrack(
    int DiscNumber, int TrackNumber, string? Title, int? DurationMs,
    string? MusicBrainzRecordingId, string? CorroboratingProviders, bool IsContested);

public sealed record AlbumReviewProgramSource(string Provider, string? AlbumId, int TrackCount, bool InWinningCluster);

/// <summary>Cached catalog reference, name-linked to owned rows. It does not establish an edition.</summary>
public sealed record AlbumReviewProgram(
    int Id, IReadOnlyList<int> RelatedSongIds, string Relation, string? Artist, string? Album,
    int? Year, string Status, DateTime? CollectedAtUtc, int ResolvedTrackCount,
    bool TrackCountContested, string SourcesState, IReadOnlyList<AlbumReviewProgramSource> Sources,
    IReadOnlyList<AlbumReviewProgramTrack> Tracks);

public sealed record AlbumReviewContext(
    string SchemaVersion, string Revision, DateTime ObservedAtUtc, AlbumReviewScope Scope,
    IReadOnlyList<AlbumReviewTrack> Tracks, IReadOnlyList<AlbumReviewDuplicate> Duplicates,
    IReadOnlyList<AlbumReviewConflict> Conflicts, IReadOnlyList<AlbumReviewProgram> Programs,
    bool EvidenceComplete, int MaxTracks, int MaxEvidenceRows, int MaxResponseBytes);

public sealed record AlbumReviewError(string Code, string Message, int? Limit = null);
