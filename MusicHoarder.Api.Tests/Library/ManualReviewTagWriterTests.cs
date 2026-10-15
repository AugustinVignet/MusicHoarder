using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class ManualReviewTagWriterTests
{
    [Theory]
    [InlineData("silence.mp3", "1999-03-15", "2000")]
    [InlineData("silence.flac", "1999-03-15", "2000")]
    [InlineData("silence.m4a", "1999-03-15", "2000")]
    [InlineData("silence.mp3", "2000-03-15", "2000-03-15")]
    [InlineData("silence.flac", "2000-03-15", "2000-03-15")]
    [InlineData("silence.m4a", "2000-03-15", "2000")]
    public async Task Explicit_album_credit_year_and_disc_survive_election_and_conflicting_date(
        string fixture, string releaseDate, string expectedDate)
    {
        await using var db = new MusicHoarderDbContext(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/source/review.mp3", FileName = "review.mp3", Extension = ".mp3",
            FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
            Title = "Example Track", Album = "Example Album", Artist = "Example Artist",
            AlbumArtist = "Example Artist", Year = 2000, TrackNumber = 1,
            ReleaseDate = releaseDate, EnrichmentStatus = EnrichmentStatus.NeedsReview, IsCompilation = true,
        };
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        await SongsEndpoints.ManualReviewTrack(song.Id, new ManualReviewRequest("approve",
            AlbumArtist: "Example Artist & Guest", Year: 2000, DiscNumber: 2), db);
        db.ChangeTracker.Clear();
        song = await db.Songs.SingleAsync();
        await ManualReviewMetadata.LoadAsync(db, [song]);
        var elected = AlbumIdentity.FromSong(song) with { AlbumArtist = "Example Artist", Year = 1990 };
        var path = Path.Combine(Path.GetTempPath(), $"mh-review-{Guid.NewGuid():N}{Path.GetExtension(fixture)}");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), path);
        try
        {
            await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, elected);
            using var file = TagLib.File.Create(path);
            Assert.Equal("Example Artist & Guest", file.Tag.JoinedAlbumArtists);
            Assert.Equal(2000u, file.Tag.Year);
            Assert.Equal(2u, file.Tag.Disc);
            if (fixture.EndsWith(".mp3", StringComparison.Ordinal))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);
                Assert.Equal([expectedDate], TagLib.Id3v2.TextInformationFrame.Get(id3, "TDRC", false).Text);
            }
            if (fixture.EndsWith(".flac", StringComparison.Ordinal))
                Assert.Equal([expectedDate], ((TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph)).GetField("DATE"));
            Assert.Equal("2000", WrittenTagSet.From(song, elected).Year);
            Assert.Equal("Example Artist & Guest", WrittenTagSet.From(song, elected).AlbumArtist);
            Assert.True(song.IsCompilation);
        }
        finally { File.Delete(path); }
    }
}
