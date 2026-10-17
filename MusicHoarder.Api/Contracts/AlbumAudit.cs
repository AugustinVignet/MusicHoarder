namespace MusicHoarder.Api.Contracts;

/// <summary>Additive B scope. Froms must be empty; explicit membership is not a musical verdict.</summary>
public sealed record AlbumAuditScope(string Version, int[] MemberSongIds, AlbumAuditReference? Reference = null);
public sealed record AlbumAuditReference(string Kind, int Id, string Revision);
public sealed record AlbumAuditContextRequest(int[] MemberSongIds, AlbumAuditReference? Reference = null,
    string? Cursor = null, int Limit = 25);
public sealed record AlbumAuditGroup(string GroupKey, string Kind, int MemberCount, int[] MemberIds,
    bool MembersComplete, string? MemberCursor, string? AlbumArtist, string? Album,
    int FromCount, int DestinationCount, int BuiltCount, int NoDestinationCount,
    IReadOnlyDictionary<string, int> StatusCounts, string DataRevision,
    IReadOnlyList<string> DispersionIndicators, IReadOnlyList<AlbumAuditRelationCandidate> RelationCandidates,
    int CandidateGroupCount, bool CandidatesComplete, string ContextReadReference);
public sealed record AlbumAuditRelationCandidate(string GroupKey, string Reason);
public sealed record AlbumAuditInventory(string SchemaVersion, string InventoryRevision, DateTime ObservedAtUtc,
    IReadOnlyList<AlbumAuditGroup> Groups, string? NextCursor, AlbumAuditCoverage Coverage);
public sealed record AlbumAuditCoverage(long ActiveTracks, long PrimaryGroups, string Definition);
public sealed record AlbumAuditRelated(AlbumReviewTrack Track, bool Blocking);
public sealed record AlbumAuditCachedReference(AlbumAuditReference Reference, AlbumReviewProgram Program);
public sealed record AlbumAuditCorrespondence(int SongId, int? DiscNumber, int? TrackNumber,
    string Criterion, string Uncertainty);
public sealed record AlbumAuditFact(string Kind, IReadOnlyList<int> SongIds, int? DiscNumber = null,
    int? TrackNumber = null, string? Detail = null);
public sealed record AlbumAuditPlanContext(AlbumReviewContextRequest Scope, string ContextRevision);
public sealed record AlbumAuditContext(string SchemaVersion, DateTime ObservedAtUtc,
    IReadOnlyList<AlbumReviewTrack> Members, IReadOnlyList<AlbumAuditRelated> Related,
    IReadOnlyList<AlbumReviewTrack> BlockingRelated,
    int RelatedCount, int BlockingRelatedCount, IReadOnlyList<int> BlockingRelatedIds,
    bool SelectionComplete, bool RelatedComplete, string? NextCursor,
    string ReferenceState, IReadOnlyList<AlbumAuditCachedReference> References,
    IReadOnlyList<AlbumAuditCorrespondence> Correspondences, IReadOnlyList<AlbumAuditFact> Facts,
    IReadOnlyList<AlbumReviewDuplicate> Duplicates, IReadOnlyList<AlbumReviewConflict> Conflicts,
    bool PlanReady, AlbumAuditPlanContext? PlanContext, string ContextRevision,
    string MembershipRevision, string EvidenceRevision, string RelatedRevision,
    bool ContentVerified, string ProgramCompleteness);

public sealed record AlbumAuditMemberPage(int[] MemberIds, string? NextCursor, string InventoryRevision);
