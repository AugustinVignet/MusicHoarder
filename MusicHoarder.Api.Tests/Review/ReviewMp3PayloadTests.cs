using System.Security.Cryptography;
using MusicHoarder.Api.Review.Copy;

namespace MusicHoarder.Api.Tests.Review;

public sealed class ReviewMp3PayloadTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"mh-mp3-boundaries-{Guid.NewGuid():N}")).FullName;
    private static byte[] Payload => [.. new byte[27], .. "MPEG bytes with interior ID3 and TAG markers"u8, .. new byte[256]];

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task ChangedBoundaryTags_DoNotChangeAnyHashedPayloadByte(byte version, bool footer)
    {
        var source = Write([.. Tag(version, footer, 17), .. Payload, .. V1(3)]);
        var candidate = Write([.. Tag(4, false, 81), .. Payload, .. V1(9)]);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Payload));
        Assert.Equal(expected, await ReviewMp3Payload.HashAsync(source, default));
        Assert.Equal(expected, await ReviewMp3Payload.HashAsync(candidate, default));
    }

    [Fact]
    public async Task UntaggedLeadingGarbageAndInteriorMarkers_AreNeverDiscarded()
    {
        var original = Payload;
        var changed = original.ToArray(); changed[0] = 1;
        Assert.NotEqual(await ReviewMp3Payload.HashAsync(Write(original), default),
            await ReviewMp3Payload.HashAsync(Write(changed), default));
        // A metadata-looking marker away from a file boundary is still part of the payload.
        changed = original.ToArray(); changed[30] ^= 1;
        Assert.NotEqual(await ReviewMp3Payload.HashAsync(Write(original), default),
            await ReviewMp3Payload.HashAsync(Write(changed), default));
    }

    [Fact]
    public async Task Id3v1IsRemovedOnlyAtTheExactEndBoundary_AndUnknownTrailersStayHashed()
    {
        var source = Write(Payload);
        var tagged = Write([.. Payload, .. V1(1)]);
        Assert.Equal(await ReviewMp3Payload.HashAsync(source, default), await ReviewMp3Payload.HashAsync(tagged, default));
        var notAtEnd = Write([.. Payload, .. V1(1), 0]);
        Assert.NotEqual(await ReviewMp3Payload.HashAsync(source, default), await ReviewMp3Payload.HashAsync(notAtEnd, default));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("flags")]
    [InlineData("size-high-bit")]
    [InlineData("oversized")]
    [InlineData("footer")]
    [InlineData("footer-size")]
    [InlineData("empty-payload")]
    [InlineData("short-header")]
    [InlineData("too-many-tags")]
    public async Task InvalidOrAmbiguousBoundary_IsRefused(string scenario)
    {
        var tag = Tag(4, scenario.StartsWith("footer"), 17);
        if (scenario == "version") tag[3] = 5;
        if (scenario == "revision") tag[4] = 255;
        if (scenario == "flags") tag[5] |= 1;
        if (scenario == "size-high-bit") tag[6] = 128;
        if (scenario == "oversized") tag[6] = 127;
        if (scenario == "footer") tag[^10] = 0;
        if (scenario == "footer-size") tag[^1] ^= 1;
        byte[] data = scenario switch
        {
            "empty-payload" => [.. tag, .. V1(0)],
            "short-header" => [.. "ID3"u8, 4],
            "too-many-tags" => [.. Enumerable.Range(0, 17).SelectMany(_ => Tag(4, false, 1)), .. Payload],
            _ => [.. tag, .. Payload]
        };
        var error = await Assert.ThrowsAsync<ReviewAudioVerifier.AudioCheckException>(() => ReviewMp3Payload.HashAsync(Write(data), default));
        Assert.Equal("invalid_mp3_metadata_boundary", error.Code);
    }

    [Fact]
    public async Task CancellationIsNotAProof()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReviewMp3Payload.HashAsync(Write(Payload), cancel.Token));
    }

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(path, bytes); return path;
    }
    private static byte[] V1(byte value) => [.. "TAG"u8, .. Enumerable.Repeat(value, 125)];
    private static byte[] Tag(byte version, bool footer, int size)
    {
        byte[] header = [.. "ID3"u8, version, 0, (byte)(footer ? 16 : 0), 0, 0, 0, (byte)size];
        byte[] tail = footer ? [.. "3DI"u8, .. header[3..]] : [];
        return [.. header, .. new byte[size], .. tail];
    }
    public void Dispose() => Directory.Delete(root, true);
}
