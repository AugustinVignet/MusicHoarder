using System.Text.Json;

namespace MusicHoarder.Api.Contracts;

/// <summary>Fields use persisted names (Title, Year, ...). Absent means unchanged;
/// JSON null means an explicitly unknown value. Approve is required for NeedsReview.</summary>
public sealed record ReviewPlanTrackRequest(int SongId, bool Approve, Dictionary<string, JsonElement> Fields, bool PreserveAlbumMembership = false);
public sealed record ReviewPlanRequest(AlbumReviewContextRequest Scope, string ContextRevision,
    string Reason, string[] EvidenceReferences, ReviewPlanTrackRequest[] Tracks);
public sealed record ReviewPlanApplyRequest(string Revision, Guid? RequestId = null);
public sealed record ReviewPlanFieldDiff(string Field, object? Before, object? After, string Intent);
public sealed record ReviewPlanTrackPreview(int SongId, string BeforeStatus, string AfterStatus,
    IReadOnlyList<ReviewPlanFieldDiff> Fields, string? CurrentPath, string PlannedPath, string Work, string From, ReviewPlanGroup BeforeGroup, ReviewPlanGroup AfterGroup);
public sealed record ReviewPlanGroup(string? AlbumArtist, string? Album);
public sealed record ReviewPlanPreview(Guid Id, string Revision, string ContextRevision,
    IReadOnlyList<ReviewPlanTrackPreview> Tracks, IReadOnlyList<int> ContextSongIds,
    IReadOnlyList<AlbumReviewConflict> Conflicts, bool CanApply, string CopyState, IReadOnlyList<AlbumReviewFileObservation> FileObservations, string PolicyVersion,
    IReadOnlyList<ReviewPlanFileFingerprint>? ContentFingerprints = null);
public sealed record ReviewPlanResult(Guid Id, string Revision, string State,
    IReadOnlyList<int> SongIds, string CopyState, Guid? JobId = null);
public sealed record ReviewPlanDrift(string Kind, int? SongId, string? Field, object? Expected, object? Actual);
public sealed record ReviewPlanError(string Code, string Message,
    IReadOnlyList<ReviewPlanDrift>? Differences = null);

public sealed record ReviewCopyTrackResult(int SongId, string State, int Attempts, string? FailureStage,
    string? FailureCode, DateTime UpdatedAtUtc, string DestinationPath, bool HasProof, string? ProofJson);
public sealed record ReviewCopyJobResult(Guid Id, Guid PlanId, Guid RequestId, string State,
    DateTime CreatedAtUtc, DateTime UpdatedAtUtc, bool MetadataApplied, bool CopiesVerified,
    string AlbumCompleteness, IReadOnlyList<ReviewCopyTrackResult> Tracks);

public sealed record ReviewPlanFileFingerprint(string Path, string Presence, string? Sha256);
