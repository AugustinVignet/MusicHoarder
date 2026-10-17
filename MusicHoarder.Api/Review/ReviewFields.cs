using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review;

/// <summary>One allowlist for review values, validation and manual field guards. No derived identity.</summary>
internal static class ReviewFields
{
    internal static readonly string[] Names =
    [
        "Title", "Artist", "Artists", "AlbumArtist", "Album", "Year", "TrackNumber", "DiscNumber",
        "TotalDiscs", "TotalTracks", "IsCompilation", "ReleaseTypePrimary", "ReleaseTypes",
        "MusicBrainzId", "MusicBrainzReleaseId", "MusicBrainzReleaseGroupId", "ArtistMusicBrainzIds",
        "AlbumArtistMusicBrainzId", "SpotifyId", "AcoustIdTrackId", "Isrc", "Genre", "ReleaseDate",
        "OriginalReleaseDate", "Label", "CatalogNumber", "Upc", "Composer", "Copyright", "ArtistSort", "AlbumArtistSort"
    ];
    private static readonly IReadOnlyDictionary<string, PropertyInfo> Properties = Names.ToDictionary(
        n => n, n => typeof(SongMetadata).GetProperty(n)!, StringComparer.Ordinal);
    internal static object? Get(SongMetadata song, string name) => Properties[name].GetValue(song);
    internal static string? Text(SongMetadata song, string name)
    {
        if (!Properties.TryGetValue(name, out var property)) return null;
        var value = property.GetValue(song);
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
    internal static void Set(SongMetadata song, string name, object? value) =>
        Properties[name].SetValue(song, name == "IsCompilation" && value is null ? false : value);
    internal static Dictionary<string, object?> Values(SongMetadata song) => Names.ToDictionary(n => n,
        n => ManualReviewMetadata.IsExplicitUnknown(song, n) ? null : Get(song, n), StringComparer.Ordinal);

    internal static object? Parse(string name, JsonElement value)
    {
        if (!Properties.TryGetValue(name, out var p)) throw Invalid($"Unsupported field: {name}.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (type == typeof(int) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            && number > 0 && number <= (name == "Year" ? 9999 : 100000)) return number;
        if (type == typeof(bool) && value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (type == typeof(string) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (text.Length is > 0 and <= 4096 && text == text.Trim() && !text.Any(char.IsControl))
            {
                if (name is "Artists" or "ArtistMusicBrainzIds" or "ReleaseTypes"
                    && text.Split(';').Any(string.IsNullOrWhiteSpace)) throw Invalid($"{name} contains an empty list member.");
                return text;
            }
        }
        throw Invalid($"Invalid {name}: use a nonempty trimmed string, positive integer, boolean, or explicit null as appropriate.");
    }

    internal static void Validate(SongMetadata song, IReadOnlySet<string> touched)
    {
        if ((touched.Contains("DiscNumber") || touched.Contains("TotalDiscs")) && song.DiscNumber.HasValue && song.TotalDiscs.HasValue && song.DiscNumber > song.TotalDiscs)
            throw Invalid("DiscNumber exceeds TotalDiscs.");
        if ((touched.Contains("TrackNumber") || touched.Contains("TotalTracks")) && song.TrackNumber.HasValue && song.TotalTracks.HasValue && song.TrackNumber > song.TotalTracks)
            throw Invalid("TrackNumber exceeds per-disc TotalTracks.");
        foreach (var date in new[] { "ReleaseDate", "OriginalReleaseDate" }.Where(touched.Contains).Select(n => (string?)Get(song, n)).Where(d => d is not null))
        {
            var formats = new[] { "yyyy", "yyyy-MM", "yyyy-MM-dd" };
            if (!DateTime.TryParseExact(date, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw Invalid("Dates must be valid YYYY, YYYY-MM or YYYY-MM-DD values.");
        }
        if ((touched.Contains("Year") || touched.Contains("ReleaseDate")) && song.ReleaseDate is not null
            && song.Year != Metadata.ReleaseDateParser.ParseYear(song.ReleaseDate))
            throw Invalid("Year and ReleaseDate must agree; explicitly clear both to make the year unknown.");
        if ((touched.Contains("ReleaseTypePrimary") || touched.Contains("ReleaseTypes"))
            && song.ReleaseTypePrimary is not null && song.ReleaseTypes is not null
            && !Metadata.MultiValue.Split(song.ReleaseTypes).Contains(song.ReleaseTypePrimary, StringComparer.OrdinalIgnoreCase))
            throw Invalid("ReleaseTypes must include the retained ReleaseTypePrimary, or explicitly clear that primary type.");
        var artists = Metadata.MultiValue.Split(song.Artists);
        var ids = Metadata.MultiValue.Split(song.ArtistMusicBrainzIds);
        if ((touched.Contains("Artists") || touched.Contains("ArtistMusicBrainzIds")) && ids.Length > 0 && (artists.Length == 0 || artists.Length != ids.Length))
            throw Invalid("ArtistMusicBrainzIds must align with the discrete Artists list.");
        foreach (var value in new[] { "MusicBrainzId", "MusicBrainzReleaseId", "MusicBrainzReleaseGroupId", "AlbumArtistMusicBrainzId" }
            .Where(touched.Contains).Select(n => (string?)Get(song, n)).Where(v => v is not null))
            if (!Guid.TryParseExact(value, "D", out _)) throw Invalid("MusicBrainz identities must be UUIDs.");
        if (touched.Contains("ArtistMusicBrainzIds") && ids.Any(id => !Guid.TryParseExact(id, "D", out _))) throw Invalid("Artist MusicBrainz identities must be UUIDs.");
    }
    private static ReviewPlanException Invalid(string message) => new(400, new("invalid_plan", message));
}
