using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class RevertMetadataChangeTests
{
    [Theory]
    [InlineData(LibraryBuildStatus.Done)]
    [InlineData(LibraryBuildStatus.Pending)]
    [InlineData(LibraryBuildStatus.Copied)]
    [InlineData(LibraryBuildStatus.Tagged)]
    [InlineData(LibraryBuildStatus.Failed)]
    public async Task Revert_restores_native_value_and_only_requeues_an_already_built_copy(LibraryBuildStatus status)
    {
        await using var db = CreateDb();
        var song = Song("changed", status);
        var other = Song("unrelated", LibraryBuildStatus.Done);
        song.LockManualApproval();
        db.Songs.AddRange(song, other);
        await db.SaveChangesAsync();
        var id = song.Id;
        var otherId = other.Id;
        var approvedAt = song.ManuallyApprovedAtUtc;
        var builtAt = song.LibraryBuiltAtUtc;
        var change = Change(song.Id);
        db.SongMetadataChanges.Add(change);
        await db.SaveChangesAsync();
        var changeId = change.Id;
        db.ChangeTracker.Clear();

        var result = await Revert(id, changeId, db);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        song = await db.Songs.SingleAsync(s => s.Id == id);
        Assert.Equal(2001, song.Year);
        Assert.Equal(status == LibraryBuildStatus.Done ? LibraryBuildStatus.Pending : status, song.LibraryBuildStatus);
        Assert.Equal(status == LibraryBuildStatus.Done ? song.DestinationPath : "/destination/earlier.mp3", song.PreviousDestinationPath);
        Assert.Equal(status == LibraryBuildStatus.Done ? 0 : 2, song.LibraryBuildAttempts);
        Assert.Equal(builtAt, song.LibraryBuiltAtUtc);
        Assert.True(song.IsManuallyApproved);
        Assert.Equal(approvedAt, song.ManuallyApprovedAtUtc);
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.NotNull((await db.SongMetadataChanges.SingleAsync()).RevertedAtUtc);
        Assert.Equal(LibraryBuildStatus.Done, (await db.Songs.SingleAsync(s => s.Id == otherId)).LibraryBuildStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revert_rejects_unapplied_or_already_reverted_changes_without_requeue(bool alreadyReverted)
    {
        await using var db = CreateDb();
        var song = Song("changed", LibraryBuildStatus.Done);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        var change = Change(song.Id);
        if (alreadyReverted) change.RevertedAtUtc = DateTime.UtcNow;
        else change.AppliedAtUtc = null;
        db.SongMetadataChanges.Add(change);
        await db.SaveChangesAsync();

        var result = await Revert(song.Id, change.Id, db);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(1966, song.Year);
        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.Equal("/destination/earlier.mp3", song.PreviousDestinationPath);
        Assert.Equal(2, song.LibraryBuildAttempts);
    }

    private static Task<IResult> Revert(int songId, int changeId, MusicHoarderDbContext db)
    {
        var method = typeof(SongsEndpoints).GetMethod("RevertMetadataChange", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Task<IResult>)method.Invoke(null, [songId, changeId, db])!;
    }

    private static SongMetadataChange Change(int songId) => new()
    {
        SongId = songId, FieldName = nameof(SongMetadata.Year), OldValue = "2001", NewValue = "1966",
        Source = "album-identity-heal", CreatedAtUtc = DateTime.UtcNow, AppliedAtUtc = DateTime.UtcNow,
    };

    private static SongMetadata Song(string name, LibraryBuildStatus status) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = $"/source/{name}.mp3", FileName = $"{name}.mp3", Extension = ".mp3",
        FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow,
        Title = "Example Track", Artist = "Example Performer", Album = "Example Collection", Year = 1966,
        EnrichmentStatus = EnrichmentStatus.Matched, LibraryBuildStatus = status,
        DestinationPath = $"/destination/{name}.mp3", PreviousDestinationPath = "/destination/earlier.mp3",
        LibraryBuildAttempts = 2, LibraryBuiltAtUtc = DateTime.UtcNow.AddDays(-1),
    };

    private static MusicHoarderDbContext CreateDb() => new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
