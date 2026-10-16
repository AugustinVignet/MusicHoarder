using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;

namespace MusicHoarder.Api.Tests.Library;

public sealed class ReviewPlanTagWriterTests
{
    [Theory]
    [InlineData("silence.mp3")]
    [InlineData("silence.flac")]
    [InlineData("silence.m4a")]
    public async Task ExplicitUnknownTagsClearSourceValues_AndDoNotReturnFromAnElectedIdentity(string fixture)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mh-plan-tags-{Guid.NewGuid():N}{Path.GetExtension(fixture)}");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), path);
        var writer = new TagLibLibraryTagWriter();
        var song = new SongMetadata
        {
            SourcePath = path, FileName = fixture, Extension = Path.GetExtension(fixture), FileSizeBytes = 1,
            IndexedAtUtc = DateTime.UtcNow, LastModifiedUtc = DateTime.UtcNow,
            Title = "Example Movement", Artist = "Artist One; Artist Two", Artists = "Artist One; Artist Two",
            AlbumArtist = "Example Ensemble", Album = "Example Program", Year = 2000,
            ReleaseDate = "2000-03-15", OriginalReleaseDate = "1990-02-01", IsCompilation = true,
            TrackNumber = 2, DiscNumber = 2, TotalDiscs = 2, TotalTracks = 10,
            MusicBrainzId = "11111111-1111-4111-8111-111111111111", MusicBrainzReleaseId = "22222222-2222-4222-8222-222222222222",
            MusicBrainzReleaseGroupId = "33333333-3333-4333-8333-333333333333",
            AlbumArtistMusicBrainzId = "44444444-4444-4444-8444-444444444444",
            ArtistMusicBrainzIds = "55555555-5555-4555-8555-555555555555; 66666666-6666-4666-8666-666666666666",
            SpotifyId = "example-spotify-id", AcoustIdTrackId = "example-acoustid-id", Isrc = "USAAA0000001",
            Genre = "Classical", Composer = "Example Composer", Label = "Example Label", CatalogNumber = "EX-1", Upc = "123456789012",
            Copyright = "Example Copyright", ArtistSort = "Artist One", AlbumArtistSort = "Example Ensemble"
        };
        try
        {
            await writer.WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));
            var elected = AlbumIdentity.FromSong(song);
            var clear = ReviewFields.Names.Where(n => n != "Artist" && n != "Title").ToArray();
            foreach (var field in clear) ReviewFields.Set(song, field, null);
            song.IsManuallyApproved = true; song.ManuallyApprovedAtUtc = DateTime.UtcNow; song.ReviewPlanId = Guid.NewGuid();
            song.ManualReviewValues = clear.ToDictionary(n => n, _ => (string?)null);
            await writer.WriteTagsAsync(path, song, elected);
            using var file = TagLib.File.Create(path);
            Assert.Equal("Example Movement", file.Tag.Title);
            Assert.Equal("Artist One & Artist Two", file.Tag.JoinedPerformers);
            Assert.Equal(0u, file.Tag.Year); Assert.Equal(0u, file.Tag.Track); Assert.Equal(0u, file.Tag.Disc);
            Assert.Equal(0u, file.Tag.TrackCount); Assert.Equal(0u, file.Tag.DiscCount);
            Assert.Empty(file.Tag.AlbumArtists);
            Assert.True(string.IsNullOrEmpty(file.Tag.Album)); Assert.True(string.IsNullOrEmpty(file.Tag.ISRC));
            Assert.True(string.IsNullOrEmpty(file.Tag.MusicBrainzTrackId));
            Assert.True(string.IsNullOrEmpty(file.Tag.MusicBrainzReleaseId));
            Assert.True(string.IsNullOrEmpty(file.Tag.MusicBrainzReleaseGroupId));
            Assert.True(string.IsNullOrEmpty(file.Tag.MusicBrainzArtistId));
            Assert.True(string.IsNullOrEmpty(file.Tag.MusicBrainzReleaseArtistId));
            Assert.Empty(file.Tag.Genres); Assert.Empty(file.Tag.Composers);
            if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            {
                Assert.Null(TagLib.Id3v2.UserTextInformationFrame.Get(id3, "ARTISTS", false));
                Assert.Null(TagLib.Id3v2.TextInformationFrame.Get(id3, "TDOR", false));
                Assert.Null(TagLib.Id3v2.TextInformationFrame.Get(id3, "TCMP", false));
                Assert.Null(TagLib.Id3v2.UserTextInformationFrame.Get(id3, "SPOTIFY_TRACK_ID", false));
                Assert.Null(TagLib.Id3v2.UserTextInformationFrame.Get(id3, "Acoustid Id", false));
            }
            if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
                foreach (var key in new[] { "ARTISTS", "DATE", "ORIGINALDATE", "ORIGINALYEAR", "COMPILATION", "SPOTIFY_TRACK_ID", "ACOUSTID_ID" })
                    Assert.Empty(xiph.GetField(key));
            if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
            {
                foreach (var key in new[] { "ARTISTS", "ORIGINALDATE", "SPOTIFY_TRACK_ID", "Acoustid Id" })
                    Assert.True(apple.GetDashBoxes("com.apple.iTunes", key)?.Length is null or 0);
                Assert.False(apple.IsCompilation);
                Assert.Empty(apple.DataBoxes("cpil"));
            }
            var written = WrittenTagSet.From(song, elected);
            Assert.Null(written.Year); Assert.Null(written.ReleaseDate); Assert.Null(written.OriginalReleaseDate);
            Assert.Null(written.IsCompilation); Assert.Null(written.Artists); Assert.Null(written.AlbumArtist);
        }
        finally { File.Delete(path); }
    }
}
