using System.Collections.Concurrent;
using System.IO.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// Aligns resolved destination paths with the letter case already present on disk. On a
/// case-insensitive mount a write to "B-Sides And Otherwise" lands in an existing
/// "B-Sides and Otherwise" folder while the row keeps the other spelling; on case-sensitive storage
/// the same drift splits one album into two folders. Reusing the on-disk spelling of an existing
/// segment keeps rows, files and folders identical. A segment with several case variants on disk and
/// no exact match is left unchanged (ambiguous). Disable with MH_DESTINATION_CASE_CANONICAL=0.
/// </summary>
public sealed class CaseCanonicalDestinationPathResolver(
    DestinationPathResolver inner, IOptions<MusicEnricherOptions> options, IFileSystem? files = null) : IDestinationPathResolver
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("MH_DESTINATION_CASE_CANONICAL") != "0";
    private static readonly TimeSpan ListingLifetime = TimeSpan.FromSeconds(10);

    private readonly IFileSystem _files = files ?? new FileSystem();
    private readonly string _root = options.Value.DestinationDirectory.TrimEnd('/');
    private readonly ConcurrentDictionary<string, (DateTime At, string[]? Names)> _listings = new(StringComparer.Ordinal);

    public string ResolvePath(SongMetadata song) => Canonicalize(inner.ResolvePath(song));

    public string ResolvePath(SongMetadata song, AlbumIdentity? albumIdentity) => Canonicalize(inner.ResolvePath(song, albumIdentity));

    public string ResolveLegacyPath(SongMetadata song) => Canonicalize(inner.ResolveLegacyPath(song));

    public string Canonicalize(string path)
    {
        if (!Enabled || _root.Length == 0 || !path.StartsWith(_root + "/", StringComparison.Ordinal)) return path;
        var segments = path[(_root.Length + 1)..].Split('/');
        var current = _root;
        for (var i = 0; i < segments.Length; i++)
        {
            var names = List(current);
            if (names is null) break;
            var segment = segments[i];
            if (!names.Contains(segment, StringComparer.Ordinal))
            {
                var variants = names.Where(n => string.Equals(n, segment, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (variants.Length != 1) break;
                segments[i] = variants[0];
            }
            current = current + "/" + segments[i];
        }
        return _root + "/" + string.Join('/', segments);
    }

    private string[]? List(string directory)
    {
        if (_listings.TryGetValue(directory, out var cached) && DateTime.UtcNow - cached.At < ListingLifetime) return cached.Names;
        string[]? names;
        try
        {
            names = _files.Directory.Exists(directory)
                ? _files.Directory.EnumerateFileSystemEntries(directory).Select(e => _files.Path.GetFileName(e)).ToArray()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { names = null; }
        _listings[directory] = (DateTime.UtcNow, names);
        return names;
    }
}
