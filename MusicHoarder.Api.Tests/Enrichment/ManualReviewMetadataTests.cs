using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Tests.Enrichment;

public class ManualReviewMetadataTests
{
    [Fact]
    public async Task Review_detail_exposes_source_and_current_disc_for_freshness_checks()
    {
        await using var db = CreateDb();
        var song = NeedsReviewSong();
        song.DiscNumber = 1;
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        await SongsEndpoints.ManualReviewTrack(song.Id, new ManualReviewRequest("approve", DiscNumber: 2), db);

        var method = typeof(SongsEndpoints).GetMethod("GetEnrichmentDetail",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = (Task<IResult>)method.Invoke(null, [song.Id, db])!;
        var result = Assert.IsAssignableFrom<IValueHttpResult>(await task);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(result.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(1, json.GetProperty("source").GetProperty("discNumber").GetInt32());
        Assert.Equal(2, json.GetProperty("current").GetProperty("discNumber").GetInt32());
        Assert.Contains(json.GetProperty("diff").EnumerateArray(),
            field => field.GetProperty("field").GetString() == "discNumber");
    }

    [Fact]
    public async Task Approve_disc_two_then_undo_restores_missing_source_disc()
    {
        await using var db = CreateDb();
        var song = NeedsReviewSong();
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await SongsEndpoints.ManualReviewTrack(song.Id,
            new ManualReviewRequest("approve", DiscNumber: 2), db);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(2, song.DiscNumber);
        Assert.Null(song.OriginalDiscNumber);
        Assert.True(song.IsManuallyApproved);
        await ManualReviewMetadata.LoadAsync(db, [song]);
        Assert.True(song.HasManualReviewValue(nameof(song.DiscNumber)));

        song.ResetEnrichment(restoreOriginal: true, force: true);
        Assert.Null(song.DiscNumber);
        Assert.False(song.IsManuallyApproved);
        await ManualReviewMetadata.LoadAsync(db, [song]);
        Assert.False(song.HasManualReviewValue(nameof(song.DiscNumber)));
    }

    [Fact]
    public async Task Approve_without_disc_preserves_existing_disc()
    {
        await using var db = CreateDb();
        var song = NeedsReviewSong();
        song.DiscNumber = 3;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        await SongsEndpoints.ManualReviewTrack(song.Id, new ManualReviewRequest("approve", Title: "Reviewed title"), db);

        Assert.Equal(3, song.DiscNumber);
        Assert.Equal(3, song.OriginalDiscNumber);
        await ManualReviewMetadata.LoadAsync(db, [song]);
        Assert.False(song.HasManualReviewValue(nameof(song.DiscNumber)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_explicitly_submitted_year_is_protected_even_when_unchanged(bool submitYear)
    {
        await using var db = CreateDb();
        var song = NeedsReviewSong();
        song.ReleaseDate = "1999-03-15";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        await SongsEndpoints.ManualReviewTrack(song.Id,
            new ManualReviewRequest("approve", Title: song.Title, Year: submitYear ? song.Year : null), db);
        db.ChangeTracker.Clear();
        song = await db.Songs.SingleAsync();
        await ManualReviewMetadata.LoadAsync(db, [song]);

        Assert.Equal(submitYear, song.HasManualReviewValue(nameof(song.Year)));
        var identity = (AlbumIdentity.FromSong(song) with { Year = 1990 }).WithManualReview(song);
        Assert.Equal(submitYear ? 2000 : 1990, identity.Year);
        Assert.Equal(submitYear ? null : "1999-03-15", ManualReviewMetadata.EffectiveReleaseDate(song));
        Assert.Equal(submitYear ? 2 : 1, await db.SongMetadataChanges.CountAsync());
    }

    [Fact]
    public async Task Old_approval_and_changed_or_reverted_values_do_not_create_overrides()
    {
        await using var db = CreateDb();
        var song = NeedsReviewSong();
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        await SongsEndpoints.ManualReviewTrack(song.Id, new ManualReviewRequest("approve", Year: 2000), db);
        await ManualReviewMetadata.LoadAsync(db, [song]);
        Assert.True(song.HasManualReviewValue(nameof(song.Year)));

        song.Year = 2001;
        Assert.False(song.HasManualReviewValue(nameof(song.Year)));
        song.Year = 2000;
        var prior = await db.SongMetadataChanges.SingleAsync();
        prior.RevertedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await ManualReviewMetadata.LoadAsync(db, [song]);
        Assert.False(song.HasManualReviewValue(nameof(song.Year)));

        prior.RevertedAtUtc = null;
        // A later approval with no explicit year cannot inherit the earlier approval's evidence.
        song.ResetEnrichment(restoreOriginal: true, force: true);
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        prior.AppliedAtUtc = prior.AppliedAtUtc!.Value.AddDays(-1);
        await db.SaveChangesAsync();
        await SongsEndpoints.ManualReviewTrack(song.Id, new ManualReviewRequest("approve", Title: song.Title), db);
        await ManualReviewMetadata.LoadAsync(db, [song]);
        Assert.False(song.HasManualReviewValue(nameof(song.Year)));
        Assert.True(song.HasManualReviewValue(nameof(song.Title)));
    }

    [Fact]
    public async Task Legacy_approval_without_field_audit_does_not_protect_inherited_year()
    {
        await using var db = CreateDb();
        var song = NeedsReviewSong();
        song.LockManualApproval();
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        await ManualReviewMetadata.LoadAsync(db, [song]);
        Assert.False(song.HasManualReviewValue(nameof(song.Year)));
    }

    private static SongMetadata NeedsReviewSong() => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/source/review.mp3",
        FileName = "review.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Example Artist",
        AlbumArtist = "Example Artist",
        Album = "Example Album",
        Title = "Example Track",
        Year = 2000,
        EnrichmentStatus = EnrichmentStatus.NeedsReview,
    };

    private static MusicHoarderDbContext CreateDb() => new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);
}
