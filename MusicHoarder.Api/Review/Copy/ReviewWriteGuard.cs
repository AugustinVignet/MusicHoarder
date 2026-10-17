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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, SemaphoreSlim> Local = new();
    private readonly long[] keys;
    private readonly NpgsqlConnection? connection;
    private ReviewWriteGuard(NpgsqlConnection? connection, params long[] keys) { this.connection = connection; this.keys = keys; }

    internal static async Task<ReviewWriteGuard?> AcquireAsync(MusicHoarderDbContext db, bool wait, CancellationToken ct)
        => await AcquireKeyAsync(db, Key, wait, ct);

    internal static Task<ReviewWriteGuard?> AcquireSongAsync(MusicHoarderDbContext db, int songId, bool wait, CancellationToken ct)
        => AcquireKeyAsync(db, 0x4d48534f00000000L | (uint)songId, wait, ct);

    private static async Task<ReviewWriteGuard?> AcquireKeyAsync(MusicHoarderDbContext db, long key, bool wait, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            var gate = Local.GetOrAdd(key, _ => new(1, 1));
            if (wait) await gate.WaitAsync(ct);
            else if (!await gate.WaitAsync(0, ct)) return null;
            return new(null, key);
        }
        var builder = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = wait ? "SELECT pg_advisory_lock(@key)" : "SELECT pg_try_advisory_lock(@key)";
            command.Parameters.AddWithValue("key", key);
            // A blocked builder must remain cancellable during shutdown.
            command.CommandTimeout = 0;
            var result = await command.ExecuteScalarAsync(ct);
            if (!wait && result is not true) { await connection.DisposeAsync(); return null; }
            return new(connection, key);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal static async Task<ReviewWriteGuard?> AcquireSongsAsync(MusicHoarderDbContext db, IEnumerable<int> ids, CancellationToken ct)
    {
        var keys = ids.Distinct().Order().Select(id => 0x4d48534f00000000L | (uint)id).ToArray();
        if (!db.Database.IsNpgsql())
        {
            var acquired = new List<long>();
            try
            {
                foreach (var key in keys)
                {
                    if (!await Local.GetOrAdd(key, _ => new(1, 1)).WaitAsync(0, ct))
                    { foreach (var held in acquired) Local[held].Release(); return null; }
                    acquired.Add(key);
                }
                return new(null, keys);
            }
            catch { foreach (var held in acquired) Local[held].Release(); throw; }
        }
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT bool_and(pg_try_advisory_lock(k)) FROM unnest(@keys) AS k";
            command.Parameters.AddWithValue("keys", keys);
            if (await command.ExecuteScalarAsync(ct) is not true) { await connection.DisposeAsync(); return null; }
            return new(connection, keys);
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
        if (connection is null) foreach (var key in keys) Local[key].Release();
        else
        {
            try
            {
                // Closing a socket can return before the backend has released its locks.
                // Await the explicit unlock so the next Apply/replay is not spuriously busy.
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT pg_advisory_unlock(k) FROM unnest(@keys) AS k";
                    command.Parameters.AddWithValue("keys", keys);
                    await command.ExecuteNonQueryAsync();
                }
            }
            finally { await connection.DisposeAsync(); } // fail-closed session cleanup if unlock failed
        }
    }
}
