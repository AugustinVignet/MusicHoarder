namespace MusicHoarder.Api.Persistence;

/// <summary>A reviewer's evidence-backed decision that one exact AI grade is a false positive.
/// Bound to that grade row: a newer grade is unacknowledged until reviewed again.</summary>
public sealed class SongGradeAcknowledgement
{
    public int Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public int SongId { get; set; }
    public int GradeId { get; set; }
    public SongQualityGrade Grade { get; set; } = null!;
    public string Reason { get; set; } = "";
    public string? EvidenceReference { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}
