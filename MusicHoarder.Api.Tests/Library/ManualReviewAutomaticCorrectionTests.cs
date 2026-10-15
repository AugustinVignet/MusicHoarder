using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class ManualReviewAutomaticCorrectionTests
{
    [Fact]
    public async Task Heal_preserves_explicit_review_values_but_repairs_other_fields_and_legacy_locks()
    {
        await using var db = CreateDb();
        var reviewed = Song("reviewed", 9, 2001);
        reviewed.TotalDiscs = null; // Not submitted: ordinary repair must still work.
        var legacy = Song("legacy", 8, 2001);
        legacy.LockManualApproval();
        var unreviewed = Song("unreviewed", 7, 2001);
        db.Songs.AddRange(reviewed, legacy, unreviewed);
        AddDominantNeighbours(db);
        await db.SaveChangesAsync();
        var reviewedId = reviewed.Id;
        var legacyId = legacy.Id;
        var unreviewedId = unreviewed.Id;
        await Review(db, reviewed, new("approve", AlbumArtist: "Various Artists", Year: 2001, DiscNumber: 3));
        db.ChangeTracker.Clear(); // Exercise loading the current audit, not an in-memory override.

        var result = await Healer(db).HealAsync();

        reviewed = await db.Songs.SingleAsync(s => s.Id == reviewedId);
        Assert.Equal(2001, reviewed.Year);
        Assert.Equal("Various Artists", reviewed.AlbumArtist);
        Assert.Equal(3, reviewed.DiscNumber);
        Assert.Equal(3, reviewed.TotalDiscs);
        Assert.True(reviewed.IsManuallyApproved);
        Assert.True(reviewed.HasManualReviewValue(nameof(reviewed.Year)));
        Assert.Equal(1966, (await db.Songs.SingleAsync(s => s.Id == legacyId)).Year);
        Assert.Equal(1966, (await db.Songs.SingleAsync(s => s.Id == unreviewedId)).Year);
        Assert.Equal(3, result.SongsCorrected);
        var automatic = await db.SongMetadataChanges.Where(c => c.Source == "album-identity-heal").ToListAsync();
        Assert.Contains(automatic, c => c.SongId == reviewedId && c.FieldName == nameof(SongMetadata.TotalDiscs));
        Assert.DoesNotContain(automatic, c => c.SongId == reviewedId
            && (c.FieldName == nameof(SongMetadata.Year) || c.FieldName == nameof(SongMetadata.AlbumArtist)));
        var tags = WrittenTagSet.From(reviewed, AlbumIdentity.FromSong(reviewed));
        Assert.Equal("2001", tags.Year);
        Assert.Equal("Various Artists", tags.AlbumArtist);
        Assert.Equal("3", tags.DiscNumber);
    }

    [Fact]
    public async Task Preview_does_not_report_changes_blocked_by_the_current_review()
    {
        await using var db = CreateDb();
        var reviewed = Song("reviewed", 9, 2001);
        db.Songs.Add(reviewed);
        AddDominantNeighbours(db);
        await db.SaveChangesAsync();
        var id = reviewed.Id;
        await Review(db, reviewed, new("approve", AlbumArtist: "Various Artists", Year: 2001, DiscNumber: 3));
        var auditCount = await db.SongMetadataChanges.CountAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await Healer(db).DetectAsync());
        Assert.Equal(auditCount, await db.SongMetadataChanges.CountAsync());
        Assert.Equal(2001, (await db.Songs.AsNoTracking().SingleAsync(s => s.Id == id)).Year);
        Assert.Equal(new AlbumSplitHealResult(0, 0, 0), await Healer(db).HealAsync());
        Assert.Equal(LibraryBuildStatus.Done, (await db.Songs.SingleAsync(s => s.Id == id)).LibraryBuildStatus);
    }

    [Theory]
    [InlineData("legacy-lock")]
    [InlineData("not-submitted")]
    [InlineData("prior-approval")]
    [InlineData("reverted")]
    [InlineData("changed-value")]
    public async Task Heal_does_not_protect_an_inherited_or_invalid_review_field(string scenario)
    {
        await using var db = CreateDb();
        var song = Song("reviewed", 9, 2001);
        db.Songs.Add(song);
        AddDominantNeighbours(db);
        await db.SaveChangesAsync();
        var id = song.Id;
        if (scenario == "legacy-lock")
            song.LockManualApproval();
        else
        {
            await Review(db, song, scenario == "not-submitted"
                ? new("approve", Title: song.Title)
                : new("approve", Year: 2001));
            if (scenario == "prior-approval")
                song.ManuallyApprovedAtUtc = song.ManuallyApprovedAtUtc!.Value.AddSeconds(1);
            if (scenario == "reverted")
                (await db.SongMetadataChanges.SingleAsync(c => c.SongId == id)).RevertedAtUtc = DateTime.UtcNow;
            if (scenario == "changed-value")
                song.Year = 2002;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Healer(db).HealAsync();

        song = await db.Songs.SingleAsync(s => s.Id == id);
        Assert.Equal(1966, song.Year);
        Assert.True(song.IsManuallyApproved); // A lock alone is deliberately not a field override.
        Assert.Contains(await db.SongMetadataChanges.ToListAsync(), c => c.SongId == id
            && c.Source == "album-identity-heal" && c.FieldName == nameof(SongMetadata.Year));
    }

    [Fact]
    public async Task Foreign_artist_restore_respects_current_review_but_still_repairs_a_legacy_lock()
    {
        await using var db = CreateDb();
        var reviewed = Song("reviewed", 1, 2001);
        var legacy = Song("legacy", 2, 2001);
        foreach (var song in new[] { reviewed, legacy })
        {
            song.IsCompilation = false;
            song.AlbumArtist = "Foreign Artist";
        }
        legacy.LockManualApproval();
        db.Songs.AddRange(reviewed, legacy);
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "example performer", AlbumKey = "example collection",
            DisplayTitle = "Unrelated Album", DisplayArtist = "Foreign Artist",
            Status = CanonicalAlbumStatus.Fetched,
        });
        await db.SaveChangesAsync();
        var reviewedId = reviewed.Id;
        var legacyId = legacy.Id;
        foreach (var song in new[] { reviewed, legacy })
            db.SongMetadataChanges.Add(new SongMetadataChange
            {
                SongId = song.Id, FieldName = nameof(SongMetadata.AlbumArtist),
                OldValue = "Example Performer", NewValue = "Foreign Artist", Source = "album-identity-heal",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1), AppliedAtUtc = DateTime.UtcNow.AddDays(-1),
            });
        await db.SaveChangesAsync();
        await Review(db, reviewed, new("approve", AlbumArtist: "Foreign Artist"));
        db.ChangeTracker.Clear();

        var result = await Healer(db).HealAsync();

        Assert.Equal(1, result.SongsCorrected);
        Assert.Equal("Foreign Artist", (await db.Songs.SingleAsync(s => s.Id == reviewedId)).AlbumArtist);
        Assert.Equal("Example Performer", (await db.Songs.SingleAsync(s => s.Id == legacyId)).AlbumArtist);
        Assert.Equal(LibraryBuildStatus.Done, (await db.Songs.SingleAsync(s => s.Id == reviewedId)).LibraryBuildStatus);
        Assert.Equal(LibraryBuildStatus.Pending, (await db.Songs.SingleAsync(s => s.Id == legacyId)).LibraryBuildStatus);
    }

    [Fact]
    public async Task Canonical_consolidation_preserves_explicit_identity_and_positions_but_fixes_unsubmitted_fields()
    {
        await using var db = CreateDb();
        var reviewed = Song("reviewed", 9, 2001);
        var inherited = Song("inherited", 8, 2001);
        db.Songs.AddRange(reviewed, inherited);
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "various artists", AlbumKey = "example collection",
            DisplayTitle = "Example Collection (Remastered)", DisplayArtist = "Artistes Divers",
            Year = 1966, Status = CanonicalAlbumStatus.Fetched,
            Tracks =
            [
                new() { Title = reviewed.Title, TrackNumber = 1, DiscNumber = 1 },
                new() { Title = inherited.Title, TrackNumber = 2, DiscNumber = 1 },
            ],
        });
        await db.SaveChangesAsync();
        var reviewedId = reviewed.Id;
        var inheritedId = inherited.Id;
        await Review(db, reviewed, new("approve", Album: reviewed.Album,
            AlbumArtist: "Various Artists", Year: 2001, TrackNumber: 9, DiscNumber: 3));
        // This approval intentionally says nothing about album, year or positions.
        await Review(db, inherited, new("approve", AlbumArtist: "Various Artists"));
        db.ChangeTracker.Clear();

        var consolidator = new CanonicalAlbumConsolidator(Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions()),
            NullLogger<CanonicalAlbumConsolidator>.Instance);
        var result = await consolidator.ConsolidateAsync(db, "Various Artists", "Example Collection", CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(2, result.Matched);
        reviewed = await db.Songs.SingleAsync(s => s.Id == reviewedId);
        Assert.Equal("Example Collection", reviewed.Album);
        Assert.Equal("Various Artists", reviewed.AlbumArtist);
        Assert.Equal(2001, reviewed.Year);
        Assert.Equal(9, reviewed.TrackNumber);
        Assert.Equal(3, reviewed.DiscNumber);
        Assert.DoesNotContain(await db.SongMetadataChanges.ToListAsync(),
            c => c.SongId == reviewedId && c.Source == "canonical-album");
        inherited = await db.Songs.SingleAsync(s => s.Id == inheritedId);
        Assert.Equal("Example Collection (Remastered)", inherited.Album);
        Assert.Equal("Various Artists", inherited.AlbumArtist);
        Assert.Equal(1966, inherited.Year);
        Assert.Equal(2, inherited.TrackNumber);
        Assert.Equal(1, inherited.DiscNumber);
        Assert.Equal(1, result.Corrected);
    }

    private static async Task Review(MusicHoarderDbContext db, SongMetadata song, ManualReviewRequest request)
    {
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        var result = await SongsEndpoints.ManualReviewTrack(song.Id, request, db);
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        song.LibraryBuildStatus = LibraryBuildStatus.Done;
        song.DestinationPath = $"/destination/{song.FileName}";
        await db.SaveChangesAsync();
    }

    private static void AddDominantNeighbours(MusicHoarderDbContext db)
    {
        for (var i = 1; i <= 4; i++)
            db.Songs.Add(Song($"neighbour-{i}", i, 1966));
    }

    private static SongMetadata Song(string name, int track, int year) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = $"/source/{name}.mp3", FileName = $"{name}.mp3", Extension = ".mp3",
        FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
        Artist = "Example Performer", AlbumArtist = "Artistes Divers", Album = "Example Collection",
        Title = name, Year = year, TrackNumber = track, DiscNumber = 1, TotalDiscs = 3, IsCompilation = true,
        EnrichmentStatus = EnrichmentStatus.Matched, LibraryBuildStatus = LibraryBuildStatus.Done,
        DestinationPath = $"/destination/{name}.mp3",
    };

    private static MusicHoarderDbContext CreateDb() => new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static IAlbumSplitHealer Healer(MusicHoarderDbContext db)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source", DestinationDirectory = "/destination", EnableCanonicalDrivenBuild = true,
        });
        return new AlbumSplitHealer(db, new AlbumIdentityReconciler(), new DestinationPathResolver(options), options,
            NullLogger<AlbumSplitHealer>.Instance);
    }
}
