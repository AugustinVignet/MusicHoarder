using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using Npgsql;

namespace MusicHoarder.Api.Review.Copy;

/// <summary>One publication gate shared by the legacy builder, Apply and verified jobs.
/// Dedicated unpooled PostgreSQL session: never return a session holding a lock to a pool.
/// Global by database, deliberately serializing publishers even across owners/instances.
/// The in-memory fallback is for unit tests only; production always uses PostgreSQL.</summary>
internal sealed class ReviewWriteGuard : IAsyncDisposable
{
    private const long Key = 0x4d48525657524954;
    private static readonly SemaphoreSlim Local = new(1, 1);
    private readonly NpgsqlConnection? connection;
    private ReviewWriteGuard(NpgsqlConnection? connection) => this.connection = connection;

    internal static async Task<ReviewWriteGuard?> AcquireAsync(MusicHoarderDbContext db, bool wait, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            if (wait) await Local.WaitAsync(ct);
            else if (!await Local.WaitAsync(0, ct)) return null;
            return new(null);
        }
        var builder = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = wait ? "SELECT pg_advisory_lock(@key)" : "SELECT pg_try_advisory_lock(@key)";
            command.Parameters.AddWithValue("key", Key);
            // A blocked builder must remain cancellable during shutdown.
            command.CommandTimeout = 0;
            var result = await command.ExecuteScalarAsync(ct);
            if (!wait && result is not true) { await connection.DisposeAsync(); return null; }
            return new(connection);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        if (connection is null) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(ct); // fail closed if the owning session was lost
    }
    public async ValueTask DisposeAsync()
    {
        if (connection is null) Local.Release();
        else await connection.DisposeAsync(); // releases the session lock, including exceptional paths
    }
}
