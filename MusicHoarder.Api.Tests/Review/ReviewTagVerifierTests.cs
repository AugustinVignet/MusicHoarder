using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Tests.Library;

namespace MusicHoarder.Api.Tests.Review;

public sealed class ReviewTagVerifierTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mh-tag-proof-" + Guid.NewGuid().ToString("N"));
    public ReviewTagVerifierTests() => Directory.CreateDirectory(root);
    [Theory]
    [InlineData("silence.mp3")]
    [InlineData("silence.flac")]
    [InlineData("silence.m4a")]
    [InlineData("silence.opus")]
    public async Task EveryManagedField_AndExplicitUnknown_IsReadBackFromNativeFormat(string fixture)
    {
        var path = Path.Combine(root, fixture); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), path);
        var song = ReviewCopyTagRetagTests.Song(); song.ReviewPlanId = Guid.NewGuid(); song.IsManuallyApproved = true;
        song.ManuallyApprovedAtUtc = DateTime.UtcNow;
        song.TotalTracks = 14; song.TotalDiscs = 2; song.IsCompilation = true;
        song.MusicBrainzId = "11111111-1111-4111-8111-111111111111";
        song.MusicBrainzReleaseId = "22222222-2222-4222-8222-222222222222";
        song.MusicBrainzReleaseGroupId = "33333333-3333-4333-8333-333333333333";
        song.AlbumArtistMusicBrainzId = "44444444-4444-4444-8444-444444444444";
        song.SpotifyId = "example-track"; song.AcoustIdTrackId = "example-acoustid"; song.Isrc = "ZZABC2600001";
        song.Genre = "Example Genre; Secondary Genre"; song.Composer = "Composer One; Composer Two";
        song.Label = "Example Label"; song.CatalogNumber = "EX-001"; song.Upc = "1234567890123";
        song.OriginalReleaseDate = "1990-05-14"; song.Copyright = "Example Copyright";
        song.ArtistSort = "Performers, Example"; song.AlbumArtistSort = "Ensemble, Example";
        var writer = new TagLibLibraryTagWriter();
        for (var i = 0; i < 3; i++) await writer.WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));
        Assert.Empty(ReviewTagVerifier.Verify(path, song));
        foreach (var field in ReviewFields.Names) ReviewFields.Set(song, field, null);
        song.ManualReviewValues = ReviewFields.Names.ToDictionary(f => f, _ => (string?)null);
        await writer.WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));
        Assert.Empty(ReviewTagVerifier.Verify(path, song));
        using (var tamper = TagLib.File.Create(path)) { tamper.Tag.Title = "Unexpected title"; tamper.Save(); }
        Assert.Contains("Title", ReviewTagVerifier.Verify(path, song));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
