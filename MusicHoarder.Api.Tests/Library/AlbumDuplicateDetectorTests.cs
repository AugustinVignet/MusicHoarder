using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class AlbumDuplicateDetectorTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task Detect_PairsLeadingTheVariants()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "The Blueprint 3", track: 1),
            Song("/a/2.mp3", album: "The Blueprint 3", track: 2),
            Song("/b/1.mp3", album: "Blueprint 3", track: 1));
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        var pair = Assert.Single(pairs);
        Assert.Equal("same title after normalization", pair.Evidence);
        Assert.Equal(
            ["Blueprint 3", "The Blueprint 3"],
            new[] { pair.AlbumA, pair.AlbumB }.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Detect_PairsAmpersandVsAnd()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "Songs of Love & Hate", track: 1),
            Song("/b/1.mp3", album: "Songs of Love and Hate", track: 1));
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        var pair = Assert.Single(pairs);
        Assert.Equal("same title after normalization", pair.Evidence);
    }

    [Fact]
    public async Task Detect_PairsFuzzyNearMiss()
    {
        // A real spelling near-miss (typo). Cosmetic variants that share a search key — trailing
        // punctuation, casing — are the split-healer's territory and are excluded here.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "My Beautiful Dark Twisted Fantasy", track: 1),
            Song("/b/1.mp3", album: "My Beautiful Dark Twistd Fantasy", track: 1));
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        var pair = Assert.Single(pairs);
        Assert.Equal("similar title", pair.Evidence);
        Assert.True(pair.FuzzyRatio >= 90);
    }

    [Fact]
    public async Task Detect_SequelNumberingNeverPairs()
    {
        // "Yeezus" and "Yeezus 2" are textually near-identical but different albums (seen on real
        // library data with the fuzzy threshold alone).
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "Yeezus", track: 1, artist: "Kanye West"),
            Song("/b/1.mp3", album: "Yeezus 2", track: 1, artist: "Kanye West"));
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        Assert.Empty(pairs);
    }

    [Fact]
    public async Task Detect_LiveTitledAlbumNeverPairsWithStudio()
    {
        // "Live" in an album title is an identity-changing qualifier even though the album-argument
        // detection path masks to packaging flags (seen on real library data: "Donda" vs
        // "Donda: Live from Chicago").
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "Donda", track: 1, artist: "Kanye West"),
            Song("/b/1.mp3", album: "Donda: Live from Chicago", track: 1, artist: "Kanye West"));
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        Assert.Empty(pairs);
    }

    [Fact]
    public async Task Detect_ShortTitlesNeverFuzzyPair()
    {
        // "ye" partial-matches almost anything; only the exact-normalized path may pair short titles.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "ye", track: 1, artist: "Kanye West"),
            Song("/b/1.mp3", album: "Yeezus", track: 1, artist: "Kanye West"));
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        Assert.Empty(pairs);
    }

    [Fact]
    public async Task Detect_DeluxeNeverPairsWithStandard()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "The Blueprint 3", track: 1),
            Song("/b/1.mp3", album: "The Blueprint 3 (Deluxe)", track: 1));
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        Assert.Empty(pairs);
    }

    [Fact]
    public async Task Detect_DifferentArtistsNeverPair()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "The Blueprint 3", track: 1, artist: "JAY-Z"),
            Song("/b/1.mp3", album: "Blueprint 3", track: 1, artist: "Someone Else"));
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        Assert.Empty(pairs);
    }

    [Fact]
    public async Task Detect_RespectsDismissal()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "The Blueprint 3", track: 1),
            Song("/b/1.mp3", album: "Blueprint 3", track: 1));
        db.DedupDismissals.Add(new DedupDismissal
        {
            OwnerUserId = Owner,
            Kind = DedupDismissalKind.AlbumPair,
            ScopeKey = AlbumGroupKey.ComputeArtistKey("JAY-Z"),
            KeyLow = "blueprint 3",
            KeyHigh = "the blueprint 3",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        Assert.Empty(pairs);
    }

    [Fact]
    public async Task Detect_ScopedToOwner()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", album: "The Blueprint 3", track: 1),
            Song("/b/1.mp3", album: "Blueprint 3", track: 1, owner: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var pairs = await Detector(db).DetectAsync(Owner);

        Assert.Empty(pairs);
    }

    // DetectAsync loads DetectColumns and nothing else. Every other column of every row here holds
    // a value, so the day Detect (or a helper under it) starts reading one, it sees that value over
    // whole rows and a blank through DetectAsync, and the two reports part.
    [Fact]
    public async Task Detect_ReadsOnlyTheColumnsItLoads()
    {
        await using var db = NewContext();
        // Keyed by the album artist, whoever performs the track.
        var guest = Song("/b/1.mp3", album: "Blueprint 3", track: 1);
        guest.Artist = "Swizz Beatz";
        // No album artist: keyed by the track artist.
        var untagged = new[]
        {
            Song("/c/1.mp3", album: "My Beautiful Dark Twisted Fantasy", track: 1, artist: "Kanye West"),
            Song("/d/1.mp3", album: "My Beautiful Dark Twistd Fantasy", track: 1, artist: "Kanye West"),
        };
        // A compilation without one is keyed as Various Artists, whoever performs each track.
        var compilation = new[]
        {
            Song("/e/1.mp3", album: "Songs of Love & Hate", track: 1, artist: "Leonard Cohen"),
            Song("/f/1.mp3", album: "Songs of Love and Hate", track: 1, artist: "Judy Collins"),
        };
        foreach (var song in untagged.Concat(compilation))
            song.AlbumArtist = null;
        foreach (var song in compilation)
            song.IsCompilation = true;
        SongMetadata[] library =
        [
            Song("/a/1.mp3", album: "The Blueprint 3", track: 1),
            Song("/a/2.mp3", album: "The Blueprint 3", track: 2),
            guest, .. untagged, .. compilation,
        ];
        string[] eligibility = [nameof(SongMetadata.DeletedAtUtc), nameof(SongMetadata.IsSynthetic), nameof(SongMetadata.IsDuplicate)];
        for (var i = 0; i < library.Length; i++)
            library[i].FillExcept(db, seed: i, AlbumDuplicateDetector.DetectColumns.Names, eligibility);
        db.Songs.AddRange(library);
        await db.SaveChangesAsync();
        var threshold = new MusicEnricherOptions().AlbumMergeFuzzyThreshold;

        var overWholeRows = AlbumDuplicateDetector.Detect(await db.Songs.AsNoTracking().ToListAsync(), [], threshold);
        var loaded = await Detector(db).DetectAsync(Owner);

        Assert.Equal(3, overWholeRows.Count);
        Assert.Equal(overWholeRows, loaded);

        // And the list is not padded: leave any one of them out and the report changes. (The owner
        // id is read as well, as part of the album key, but one owner's rows all carry the same one.)
        foreach (var column in AlbumDuplicateDetector.DetectColumns.Names.Except([nameof(SongMetadata.OwnerUserId)]))
        {
            var without = new SongColumnSet([.. AlbumDuplicateDetector.DetectColumns.Names.Where(n => n != column)]);
            var rows = await db.Songs.WithOnly(db, without).ToListAsync();
            Assert.NotEqual(overWholeRows, AlbumDuplicateDetector.Detect(rows, [], threshold));
        }
    }

    private static AlbumDuplicateDetector Detector(MusicHoarderDbContext db) => new(
        db,
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions()));

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static SongMetadata Song(
        string sourcePath,
        string album,
        int track,
        string artist = "JAY-Z",
        Guid? owner = null) => new()
    {
        OwnerUserId = owner ?? Owner,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Title = $"Track {track}",
        TrackNumber = track,
    };
}
