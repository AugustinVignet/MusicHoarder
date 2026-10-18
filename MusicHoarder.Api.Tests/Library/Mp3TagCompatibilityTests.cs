using System.Text;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Tests.Review;

namespace MusicHoarder.Api.Tests.Library;

public sealed class Mp3TagCompatibilityTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"mh-mp3-tags-{Guid.NewGuid():N}")).FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingId3v1_PreservesEveryByteAfterLeadingMetadata_IncludingPartialMpegFrame(bool legacyTag)
    {
        var audio = AudioWithPartialFinalFrame();
        var path = Create("partial", legacyTag ? LegacyTag(LegacyFrame("TT2", [0, .. "Old title"u8])) : [], audio);
        var song = ReviewedSong();

        for (var pass = 0; pass < 3; pass++)
        {
            await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(audio, bytes[LeadingTagLength(bytes)..]);
            using var file = TagLib.File.Create(path);
            Assert.Equal(TagLib.TagTypes.None, file.TagTypesOnDisk & TagLib.TagTypes.Id3v1);
            Assert.Empty(ReviewTagVerifier.Verify(path, song));
        }
    }

    [Fact]
    public async Task ExistingId3v1_IsRetained_WithoutChangingAudioOrDependingOnItsFallback()
    {
        var audio = AudioWithPartialFinalFrame();
        byte[] id3v1 = [.. "TAG"u8, .. new byte[125]];
        var path = Create("existing-v1", [], [.. audio, .. id3v1]);
        var song = ReviewedSong();

        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(audio, bytes[LeadingTagLength(bytes)..^128]);
        Assert.True(bytes.AsSpan(bytes.Length - 128, 3).SequenceEqual("TAG"u8));
        using var file = TagLib.File.Create(path);
        Assert.Equal(TagLib.TagTypes.Id3v1, file.TagTypesOnDisk & TagLib.TagTypes.Id3v1);
        AssertReviewedId3((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false), song);
    }

    [Fact]
    public async Task UnknownV22Frames_ArePreservedReversibly_AndAllReviewedFieldsSurviveConversionAndRetag()
    {
        byte[] first = LegacyFrame("CM1", [0, .. "Bon"u8]);
        byte[] second = LegacyFrame("CM1", [1, 0xff, 0, 2, 3]);
        byte[] third = LegacyFrame("ZZZ", [0, 17, 0, 255]);
        var audio = AudioWithPartialFinalFrame();
        var path = Create("unknown-v22", LegacyTag(first, LegacyFrame("TT2", [0, .. "Old title"u8]), second, third), audio);
        var song = ReviewedSong();

        using (var original = TagLib.File.Create(path))
        {
            var legacy = (TagLib.Id3v2.Tag)original.GetTag(TagLib.TagTypes.Id3v2, false);
            Assert.Equal(3, legacy.GetFrames<TagLib.Id3v2.UnknownFrame>().Count());
        }

        for (var pass = 0; pass < 3; pass++)
        {
            await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));
            Assert.Empty(ReviewTagVerifier.Verify(path, song));
            using var file = TagLib.File.Create(path);
            Assert.Equal(TagLib.TagTypes.None, file.TagTypesOnDisk & TagLib.TagTypes.Id3v1);
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false);
            AssertReviewedId3(id3, song);
            Assert.All(id3.GetFrames(), frame => Assert.Equal(4, frame.FrameId.Count));
            var preserved = id3.GetFrames<TagLib.Id3v2.PrivateFrame>().ToArray();
            Assert.Equal(3, preserved.Length);
            Assert.Equal(3, preserved.Select(f => f.Owner).Distinct().Count());
            Assert.Equal(first, preserved[0].PrivateData.Data);
            Assert.Equal(second, preserved[1].PrivateData.Data);
            Assert.Equal(third, preserved[2].PrivateData.Data);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(audio, bytes[LeadingTagLength(bytes)..]);
        }

        // Explicit unknowns must still clear every reviewed native field without losing the
        // archived source frames or resurrecting values through an ID3v1 fallback.
        foreach (var field in ReviewFields.Names) ReviewFields.Set(song, field, null);
        song.SyncedLyrics = null;
        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));
        Assert.Empty(ReviewTagVerifier.Verify(path, song));
        using var cleared = TagLib.File.Create(path);
        var clearedId3 = (TagLib.Id3v2.Tag)cleared.GetTag(TagLib.TagTypes.Id3v2, false);
        Assert.Equal(new[] { first, second, third }, clearedId3.GetFrames<TagLib.Id3v2.PrivateFrame>()
            .Select(frame => frame.PrivateData.Data).ToArray());
        var clearedBytes = File.ReadAllBytes(path);
        Assert.Equal(audio, clearedBytes[LeadingTagLength(clearedBytes)..]);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task UnknownFourCharacterFrame_AndExistingPrivateData_SurviveUnchanged(byte version)
    {
        var path = Create("unknown-modern", [], AudioWithPartialFinalFrame());
        byte[] unknown = [0, 17, 0xff, 0, 99];
        byte[] privateData = [0, 255, 16, 0];
        using (var seed = TagLib.File.Create(path))
        {
            seed.RemoveTags(TagLib.TagTypes.Id3v1);
            var id3 = (TagLib.Id3v2.Tag)seed.GetTag(TagLib.TagTypes.Id3v2, true);
            id3.Version = version;
            id3.AddFrame(new TagLib.Id3v2.UnknownFrame("XABC", new TagLib.ByteVector(unknown)));
            id3.AddFrame(new TagLib.Id3v2.PrivateFrame("example-owner", new TagLib.ByteVector(privateData)));
            seed.Save();
        }
        var song = ReviewedSong();
        await new TagLibLibraryTagWriter().WriteTagsAsync(path, song, AlbumIdentity.FromSong(song));

        Assert.Empty(ReviewTagVerifier.Verify(path, song));
        using var file = TagLib.File.Create(path);
        var tagged = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false);
        Assert.Equal(unknown, Assert.Single(tagged.GetFrames<TagLib.Id3v2.UnknownFrame>()).Data.Data);
        Assert.Equal(privateData, Assert.Single(tagged.GetFrames<TagLib.Id3v2.PrivateFrame>()).PrivateData.Data);
    }

    [AudioToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialFinalMpegFrame_RetainsEncodedPacketsAndDecodedPcm(bool legacyTag)
    {
        var source = Create("audio-source", legacyTag ? LegacyTag(LegacyFrame("CM1", [0, .. "Bon"u8])) : [],
            AudioWithPartialFinalFrame());
        var candidate = Path.Combine(root, "audio-candidate.mp3");
        File.Copy(source, candidate);
        var song = ReviewedSong();
        var original = File.ReadAllBytes(source);

        await new TagLibLibraryTagWriter().WriteTagsAsync(candidate, song, AlbumIdentity.FromSong(song));

        var proof = await new ReviewAudioVerifier(new ReviewAudioCommandRunner(AudioTools.Ffmpeg!))
            .VerifyAsync(source, candidate);
        Assert.True(proof.Verified, $"{proof.FailedStage}:{proof.FailureCode}");
        Assert.Equal(proof.Source!.EncodedAudioSha256, proof.Candidate!.EncodedAudioSha256);
        Assert.Equal(proof.Source.DecodedAudioSha256, proof.Candidate.DecodedAudioSha256);
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    private static void AssertReviewedId3(TagLib.Id3v2.Tag id3, SongMetadata song)
    {
        // Read the native v2 tag directly: a newly created v1 tag must never hide a broken v2 header.
        Assert.Equal(4, id3.Version);
        Assert.Equal(song.Title, id3.Title);
        Assert.Equal([song.AlbumArtist!], id3.AlbumArtists);
        Assert.Equal(song.MusicBrainzId, id3.MusicBrainzTrackId);
        Assert.Equal(["Alice", "Bob"], TagLib.Id3v2.UserTextInformationFrame.Get(id3, "ARTISTS", false)!.Text);
        Assert.Equal(song.SyncedLyrics, id3.Lyrics);
    }

    private string Create(string name, byte[] tag, byte[] audio)
    {
        var path = Path.Combine(root, name + ".mp3");
        File.WriteAllBytes(path, [.. tag, .. audio]);
        return path;
    }

    private static byte[] AudioWithPartialFinalFrame()
    {
        var fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.mp3"));
        var audio = fixture[LeadingTagLength(fixture)..];
        Assert.True(audio.AsSpan(0, 2).SequenceEqual(new byte[] { 0xff, 0xfb }));
        // Keep all original silent frames, 27 leading unclassified bytes, and 128 bytes of
        // another MPEG frame. Adding a trailing TAG here changes ffmpeg's last encoded packet.
        return [.. new byte[27], .. audio, .. audio[..128]];
    }

    private static int LeadingTagLength(byte[] bytes) => bytes.AsSpan(0, 3).SequenceEqual("ID3"u8)
        ? 10 + (bytes[6] << 21) + (bytes[7] << 14) + (bytes[8] << 7) + bytes[9] : 0;

    private static byte[] LegacyFrame(string id, byte[] data) =>
        [.. Encoding.ASCII.GetBytes(id), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. data];

    private static byte[] LegacyTag(params byte[][] frames)
    {
        var body = frames.SelectMany(f => f).ToArray();
        return [.. "ID3"u8, 2, 0, 0, (byte)(body.Length >> 21), (byte)((body.Length >> 14) & 127),
            (byte)((body.Length >> 7) & 127), (byte)(body.Length & 127), .. body];
    }

    private static SongMetadata ReviewedSong() => new()
    {
        SourcePath = "/source/example.mp3", FileName = "example.mp3", Extension = ".mp3", FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow, ReviewPlanId = Guid.NewGuid(),
        Title = "A reviewed title", Artist = "Alice feat. Bob", Artists = "Alice; Bob", AlbumArtist = "Alice",
        Album = "A reviewed anthology", Year = 2001, TrackNumber = 3, TotalTracks = 12, DiscNumber = 2, TotalDiscs = 3,
        IsCompilation = true, ReleaseTypePrimary = "album", ReleaseTypes = "album; compilation",
        MusicBrainzId = "00000000-0000-4000-8000-000000000001", MusicBrainzReleaseId = "00000000-0000-4000-8000-000000000002",
        MusicBrainzReleaseGroupId = "00000000-0000-4000-8000-000000000003",
        ArtistMusicBrainzIds = "00000000-0000-4000-8000-000000000004; 00000000-0000-4000-8000-000000000005",
        AlbumArtistMusicBrainzId = "00000000-0000-4000-8000-000000000004", SpotifyId = "example-track", AcoustIdTrackId = "example-acoustid",
        Isrc = "GBABC0100001", Genre = "Soul; Jazz", ReleaseDate = "2001-03-15", OriginalReleaseDate = "1984-06",
        Label = "Example label", CatalogNumber = "EX-001", Upc = "0000000000001", Composer = "Composer One; Composer Two",
        Copyright = "Example copyright", ArtistSort = "Alice and Bob", AlbumArtistSort = "Alice, Example", SyncedLyrics = "[00:01.00]Example line"
    };

    public void Dispose() => Directory.Delete(root, true);
}
