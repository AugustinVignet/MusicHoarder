using System.Security.Cryptography;

namespace MusicHoarder.Api.Review.Copy;

/// <summary>Hashes every byte outside recognized boundary ID3 tags. In a short final
/// MPEG packet, ffmpeg can include trailing ID3v1 bytes in its encoded stream hash.</summary>
internal static class ReviewMp3Payload
{
    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var (start, end) = await RangeAsync(input, ct);

        input.Position = start;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        for (var remaining = end - start; remaining > 0;)
        {
            var count = (int)Math.Min(remaining, buffer.Length);
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
            hash.AppendData(buffer.AsSpan(0, count));
            remaining -= count;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The [start, end) byte range between the boundary ID3 tags, for decoding exactly
    /// the hashed payload: a truncated final frame must not be completed with ID3v1 bytes.</summary>
    internal static async Task<(long Start, long End)> RangeAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        return await RangeAsync(input, ct);
    }

    private static async Task<(long Start, long End)> RangeAsync(FileStream input, CancellationToken ct)
    {
        long start = 0, end = input.Length;
        var header = new byte[10];
        if (end >= 128)
        {
            input.Position = end - 128;
            await input.ReadExactlyAsync(header.AsMemory(0, 3), ct);
            if (header.AsSpan(0, 3).SequenceEqual("TAG"u8)) end -= 128;
        }

        var tags = 0;
        while (end - start >= 3)
        {
            input.Position = start;
            await input.ReadExactlyAsync(header.AsMemory(0, 3), ct);
            if (!header.AsSpan(0, 3).SequenceEqual("ID3"u8)) break;
            if (++tags > 16 || end - start < 10) Invalid();
            input.Position = start;
            await input.ReadExactlyAsync(header, ct);
            var allowedFlags = header[3] switch { 2 => 0xc0, 3 => 0xe0, 4 => 0xf0, _ => -1 };
            if (allowedFlags < 0 || header[4] == 255 || (header[5] & ~allowedFlags) != 0
                || ((header[6] | header[7] | header[8] | header[9]) & 0x80) != 0) Invalid();
            var size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            var footer = header[3] == 4 && (header[5] & 0x10) != 0;
            var length = 10L + size + (footer ? 10 : 0);
            if (length > end - start) Invalid();
            if (footer)
            {
                var tail = new byte[10];
                input.Position = start + 10L + size;
                await input.ReadExactlyAsync(tail, ct);
                if (!tail.AsSpan(0, 3).SequenceEqual("3DI"u8)
                    || !tail.AsSpan(3).SequenceEqual(header.AsSpan(3))) Invalid();
            }
            start += length;
        }
        if (start >= end) Invalid();
        return (start, end);
    }

    private static void Invalid() => throw new ReviewAudioVerifier.AudioCheckException("invalid_mp3_metadata_boundary");
}
