namespace MusicHoarder.Api.Persistence;

/// <summary>Immutable preview plus a metadata application receipt. C links its durable copy job atomically.
/// Request/snapshot/preview contain only the bounded owner-authorized album context.</summary>
public sealed class ReviewPlan
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Revision { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string ContextJson { get; set; } = "";
    public string PreviewJson { get; set; } = "";
    public DateTime? AppliedAtUtc { get; set; }
}
