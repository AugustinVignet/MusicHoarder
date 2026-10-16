using MusicHoarder.Api.Library;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Review.Copy;

/// <summary>Independent readback of every plan-editable native tag, including unknowns.
/// Standard M4A year stays year-only; RELEASEDATE preserves its full precision separately.</summary>
internal static class ReviewTagVerifier
{
    internal const string Version = "review-tags-v1";
    internal static IReadOnlyList<string> Verify(string path, SongMetadata song)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        var failures = new List<string>();
        var apple = file.GetTag(TagLib.TagTypes.Apple, false) as TagLib.Mpeg4.AppleTag;
        var id3 = file.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
        var xiph = file.GetTag(TagLib.TagTypes.Xiph, false) as TagLib.Ogg.XiphComment;
        if (apple is null && id3 is null && xiph is null) return ["unsupported_tag_format"];
        void Equal(string field, string? actual, string? expected)
        { if ((actual ?? "") != (expected ?? "")) failures.Add(field); }
        void List(string field, IEnumerable<string>? actual, IEnumerable<string> expected)
        { if (!(actual ?? []).SequenceEqual(expected, StringComparer.Ordinal)) failures.Add(field); }
        string[] One(string? value) => string.IsNullOrEmpty(value) ? [] : [value];
        string[] Native(string mp3, string vorbis, string m4a, bool frame = false)
        {
            if (apple is not null)
            {
                var parents = apple.Where(b => b.BoxType == "----"
                    && (b.GetChild("mean") as TagLib.Mpeg4.AppleAdditionalInfoBox)?.Text == "com.apple.iTunes"
                    && string.Equals((b.GetChild("name") as TagLib.Mpeg4.AppleAdditionalInfoBox)?.Text, m4a, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (parents.Length > 1) failures.Add("repeated_atom:" + m4a);
                return parents.SelectMany(b => b.Children.OfType<TagLib.Mpeg4.AppleDataBox>()).Select(b => b.Text).ToArray();
            }
            if (xiph is not null) return xiph.GetField(vorbis);
            return frame ? TagLib.Id3v2.TextInformationFrame.Get(id3!, mp3, false)?.Text ?? []
                : TagLib.Id3v2.UserTextInformationFrame.Get(id3!, mp3, false)?.Text ?? [];
        }
        Equal("Title", tag.Title, song.Title); Equal("Album", tag.Album, song.Album);
        List("Artist", tag.Performers, One(TagLibLibraryTagWriter.BuildDisplayArtist(song.Artist))); List("AlbumArtist", tag.AlbumArtists, One(song.AlbumArtist));
        List("Artists", Native("ARTISTS", "ARTISTS", "ARTISTS"), MultiValue.Split(song.Artists));
        List("AlbumArtists", Native("ALBUMARTISTS", "ALBUMARTISTS", "ALBUMARTISTS"), One(song.AlbumArtist));
        void Number(string field, uint actual, int? expected) { if (actual != (uint)(expected ?? 0)) failures.Add(field); }
        Number("Year", tag.Year, song.Year); Number("TrackNumber", tag.Track, song.TrackNumber);
        Number("TotalTracks", tag.TrackCount, song.TotalTracks); Number("DiscNumber", tag.Disc, song.DiscNumber);
        Number("TotalDiscs", tag.DiscCount, song.TotalDiscs);
        Equal("MusicBrainzId", tag.MusicBrainzTrackId, song.MusicBrainzId);
        Equal("MusicBrainzReleaseId", tag.MusicBrainzReleaseId, song.MusicBrainzReleaseId);
        Equal("MusicBrainzReleaseGroupId", tag.MusicBrainzReleaseGroupId, song.MusicBrainzReleaseGroupId);
        Equal("AlbumArtistMusicBrainzId", tag.MusicBrainzReleaseArtistId, song.AlbumArtistMusicBrainzId);
        List("ArtistMusicBrainzIds", Native("MusicBrainz Artist Id", "MUSICBRAINZ_ARTISTID", "MusicBrainz Artist Id"), MultiValue.Split(song.ArtistMusicBrainzIds));
        if (xiph is not null) List("Isrc", xiph.GetField("ISRC"), One(song.Isrc));
        else Equal("Isrc", tag.ISRC, song.Isrc);
        List("Genre", tag.Genres, MultiValue.Split(song.Genre)); List("Composer", tag.Composers, MultiValue.Split(song.Composer));
        Equal("Copyright", tag.Copyright, song.Copyright);
        List("ArtistSort", tag.PerformersSort, One(song.ArtistSort)); List("AlbumArtistSort", tag.AlbumArtistsSort, One(song.AlbumArtistSort));
        List("ReleaseTypes", Native("MusicBrainz Album Type", "RELEASETYPE", "MusicBrainz Album Type"), MultiValue.Split(song.ReleaseTypes));
        List("ReleaseTypePrimary", Native("RELEASETYPE_PRIMARY", "RELEASETYPE_PRIMARY", "RELEASETYPE_PRIMARY"), One(song.ReleaseTypePrimary));
        List("Label", Native("TPUB", "LABEL", "LABEL", true), One(song.Label));
        List("CatalogNumber", Native("CATALOGNUMBER", "CATALOGNUMBER", "CATALOGNUMBER"), One(song.CatalogNumber));
        List("Upc", Native("BARCODE", "BARCODE", "BARCODE"), One(song.Upc));
        List("SpotifyId", Native("SPOTIFY_TRACK_ID", "SPOTIFY_TRACK_ID", "SPOTIFY_TRACK_ID"), One(song.SpotifyId));
        List("AcoustIdTrackId", Native("Acoustid Id", "ACOUSTID_ID", "Acoustid Id"), One(song.AcoustIdTrackId));
        var effective = ManualReviewMetadata.EffectiveReleaseDate(song);
        List("ReleaseDate", Native("TDRC", "DATE", "RELEASEDATE", true), One(apple is null ? effective ?? song.Year?.ToString() : effective));
        List("OriginalReleaseDate", Native("TDOR", "ORIGINALDATE", "ORIGINALDATE", true), One(song.OriginalReleaseDate));
        var compilation = apple is not null ? apple.IsCompilation : Native("TCMP", "COMPILATION", "", true).Contains("1");
        if (compilation != song.IsCompilation) failures.Add("IsCompilation");
        return failures;
    }
}
