using Microsoft.AspNetCore.DataProtection;
namespace MusicHoarder.Api.Review;

/// <summary>Uses the native persisted Data Protection key ring, with a separate purpose.</summary>
internal sealed class AlbumAuditCursorCodec(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("MusicHoarder.AlbumAudit.Cursor.v1");
    internal string Protect(string value) => protector.Protect(value);
    internal string Unprotect(string value) => protector.Unprotect(value);
    // Direct reader tests have no application service provider. Production endpoints inject the
    // codec backed by the same persistent key ring as native authentication.
    internal static AlbumAuditCursorCodec ForTests { get; } = new(new EphemeralDataProtectionProvider());
}
