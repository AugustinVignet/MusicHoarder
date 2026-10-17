using System.Text.Json;

namespace MusicHoarder.Api.Contracts;

/// <summary>Fields use persisted names (Title, Year, ...). Absent means unchanged;
/// JSON null means an explicitly unknown value. Approve is required for NeedsReview.</summary>
public sealed record ReviewPlanTrackRequest(int SongId, bool Approve, Dictionary<string, JsonElement> Fields);
public sealed record ReviewPlanRequest(AlbumReviewContextRequest Scope, string ContextRevision,
    string Reason, string[] EvidenceReferences, ReviewPlanTrackRequest[] Tracks);
public sealed record ReviewPlanApplyRequest(string Revision);
public sealed record ReviewPlanFieldDiff(string Field, object? Before, object? After, string Intent);
public sealed record ReviewPlanTrackPreview(int SongId, string BeforeStatus, string AfterStatus,
    IReadOnlyList<ReviewPlanFieldDiff> Fields, string? CurrentPath, string PlannedPath, string Work, string From, ReviewPlanGroup BeforeGroup, ReviewPlanGroup AfterGroup);
public sealed record ReviewPlanGroup(string? AlbumArtist, string? Album);
public sealed record ReviewPlanPreview(Guid Id, string Revision, string ContextRevision,
    IReadOnlyList<ReviewPlanTrackPreview> Tracks, IReadOnlyList<int> ContextSongIds,
    IReadOnlyList<AlbumReviewConflict> Conflicts, bool CanApply, string CopyState, IReadOnlyList<AlbumReviewFileObservation> FileObservations, string PolicyVersion);
public sealed record ReviewPlanResult(Guid Id, string Revision, string State,
    IReadOnlyList<int> SongIds, string CopyState);
public sealed record ReviewPlanDrift(string Kind, int? SongId, string? Field, object? Expected, object? Actual);
public sealed record ReviewPlanError(string Code, string Message,
    IReadOnlyList<ReviewPlanDrift>? Differences = null);
