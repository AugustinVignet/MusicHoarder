using MusicHoarder.Api.Review;
using MusicHoarder.Api.Review.Copy;
using MusicHoarder.Api.Tests.Endpoints;
using MusicHoarder.Api.Tests.Library;
using Npgsql;
using Store = MusicHoarder.Api.Tests.Endpoints.AlbumReviewContextTests.TestDatabase;

namespace MusicHoarder.Api.Tests.Review;

public sealed class ReviewTimestampTests
{
    [PostgresTheory]
    [InlineData(2026, 1234560)]
    [InlineData(2026, 1234567)]
    [InlineData(2000, 7)]
    [InlineData(2000, -7)]
    [InlineData(1999, 1234567)]
    public async Task SnapshotDatesUseExactlyTheTimestampPersistedByNpgsql(int year, int ticks)
    {
        await using var store = await Store.CreateAsync();
        var at = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(ticks);
        await using var connection = new NpgsqlConnection(store.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT @at::timestamp with time zone", connection);
        command.Parameters.AddWithValue("at", at);
        var stored = Assert.IsType<DateTime>(await command.ExecuteScalarAsync());
        Assert.Equal(stored, ReviewTimestamp.Persisted(at));
        var song = ReviewCopyTagRetagTests.Song();
        song.ManuallyApprovedAtUtc = at; song.LibraryBuiltAtUtc = at;
        var before = ReviewCopyJobs.Snapshot(song);
        var snapshot = ReviewCopyJobs.Read(before);
        Assert.Equal(stored, snapshot.ApprovedAtUtc); Assert.Equal(stored, snapshot.BuiltAtUtc);
        song.ManuallyApprovedAtUtc = stored; song.LibraryBuiltAtUtc = stored;
        Assert.Equal(before, ReviewCopyJobs.Snapshot(song));
    }

    [Fact]
    public void UnknownDatesStayUnknown()
    {
        var song = ReviewCopyTagRetagTests.Song();
        song.ManuallyApprovedAtUtc = null; song.LibraryBuiltAtUtc = null;
        var snapshot = ReviewCopyJobs.Read(ReviewCopyJobs.Snapshot(song));
        Assert.Null(snapshot.ApprovedAtUtc); Assert.Null(snapshot.BuiltAtUtc);
    }
}
