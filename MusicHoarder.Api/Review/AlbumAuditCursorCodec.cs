using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
namespace MusicHoarder.Api.Review;

/// <summary>Uses the native persisted key ring, with compressed, purpose-separated cursors.</summary>
internal sealed class AlbumAuditCursorCodec(IDataProtectionProvider provider)
{
    private const int MaxPayloadBytes = 32768;
    private readonly IDataProtector protector = provider.CreateProtector("MusicHoarder.AlbumAudit.Cursor.v2");
    internal string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxPayloadBytes) throw TooLarge();
        using var output = new MemoryStream();
        using (var compressed = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)) compressed.Write(bytes);
        var token = WebEncoders.Base64UrlEncode(protector.Protect(output.ToArray()));
        if (token.Length > 8192) throw TooLarge();
        return token;
    }
    internal string Unprotect(string value)
    {
        var bytes = protector.Unprotect(WebEncoders.Base64UrlDecode(value));
        using var input = new BrotliStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxPayloadBytes) throw new System.Security.Cryptography.CryptographicException("Invalid cursor payload.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private static AlbumReviewReadException TooLarge() => new(413, "evidence_too_large",
        "The stored navigation key exceeds the bounded cursor budget. Inspect explicit member IDs; no partial page was returned.", 8192);
    // Direct reader tests have no app services. Production injects the native persistent key ring.
    internal static AlbumAuditCursorCodec ForTests { get; } = new(new EphemeralDataProtectionProvider());
}
