namespace MusicHoarder.Api.Persistence;

/// <summary>Explicit, audited release of a destination path that only historical review copies
/// still reserve. The listed jobs no longer block that path; they can no longer be rolled back for it.</summary>
public sealed class ReviewDestinationRelease
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid RequestId { get; set; }
    public string DestinationPath { get; set; } = "";
    /// <summary>Lower-invariant path, the same folding the reservation checks use.</summary>
    public string FoldedPath { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid ActorUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<Guid> ReleasedJobIds { get; set; } = [];
    public List<int> ClearedPreviousSongIds { get; set; } = [];
}
