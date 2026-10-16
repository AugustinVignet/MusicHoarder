using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public sealed class ReviewCopyTagRetagTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task M4aRepeatedRetag_ReplacesEveryDashValue_WithoutDuplicateArtists(int writes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mh-review-retag-{Guid.NewGuid():N}.m4a");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.m4a"), path);
        var song = Song();
        try
        {
            var writer = new TagLibLibraryTagWriter();
            for (var i = 0; i < writes; i++)
                await writer.WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));
            using var file = TagLib.File.Create(path);
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, false);
            Assert.Single(apple, b => b.BoxType == "----"
                && (b.GetChild("mean") as TagLib.Mpeg4.AppleAdditionalInfoBox)?.Text == "com.apple.iTunes"
                && (b.GetChild("name") as TagLib.Mpeg4.AppleAdditionalInfoBox)?.Text == "ARTISTS");
            Assert.Equal(2, apple.DataBoxes("com.apple.iTunes", "ARTISTS").Count());
            Assert.Equal(["Performer One", "Performer Two"], apple.GetDashBoxes("com.apple.iTunes", "ARTISTS"));
            Assert.Equal(["album", "live"], apple.GetDashBoxes("com.apple.iTunes", "MusicBrainz Album Type"));
            Assert.Equal(["11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"],
                apple.GetDashBoxes("com.apple.iTunes", "MusicBrainz Artist Id"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task M4aRetag_ChangesListLengthAndClearsAllOldAtoms_WithoutLosingCollaborators()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mh-review-retag-{Guid.NewGuid():N}.m4a");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.m4a"), path);
        try
        {
            // Seed the actual malformed structure emitted by the old plural setter.
            using (var seed = TagLib.File.Create(path))
            {
                var apple = (TagLib.Mpeg4.AppleTag)seed.GetTag(TagLib.TagTypes.Apple, true);
                apple.SetDashBoxes("com.apple.iTunes", "ARTISTS", ["Old One", "Old Two"]);
                seed.Save();
            }
            var song = Song(); var writer = new TagLibLibraryTagWriter();
            foreach (var count in new[] { 1, 3, 70, 0 })
            {
                var names = Enumerable.Range(1, count).Select(i => $"Example Performer {i}").ToArray();
                song.Artists = count == 0 ? null : string.Join("; ", names);
                song.ArtistMusicBrainzIds = null;
                await writer.WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));
                using var file = TagLib.File.Create(path);
                var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, false);
                Assert.Equal(names, apple.DataBoxes("com.apple.iTunes", "ARTISTS").Select(b => b.Text).ToArray());
                Assert.Equal(count == 0 ? 0 : 1, apple.Count(b => b.BoxType == "----"
                    && (b.GetChild("name") as TagLib.Mpeg4.AppleAdditionalInfoBox)?.Text == "ARTISTS"));
            }
        }
        finally { File.Delete(path); }
    }

    internal static SongMetadata Song() => new()
    {
        SourcePath = "/source/example.m4a", FileName = "example.m4a", Extension = ".m4a", FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
        Title = "Example Movement", Artist = "Performer One & Performer Two", Artists = "Performer One; Performer Two",
        AlbumArtist = "Example Ensemble", Album = "Example Program", Year = 2000, ReleaseDate = "2000-03-15",
        TrackNumber = 1, DiscNumber = 1, ReleaseTypePrimary = "album", ReleaseTypes = "album; live",
        ArtistMusicBrainzIds = "11111111-1111-4111-8111-111111111111; 22222222-2222-4222-8222-222222222222"
    };
}
