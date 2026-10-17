namespace MusicHoarder.Api.Review;

/// <summary>The timestamp represented by Npgsql/PostgreSQL, also used before SaveChanges.
/// PostgreSQL stores whole microseconds since 2000-01-01; Npgsql drops fractional
/// microseconds towards that epoch. Do not use a tolerance: the next persisted
/// microsecond still identifies a different approval.</summary>
internal static class ReviewTimestamp
{
    private static readonly long EpochTicks = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    internal static DateTime Persisted(DateTime value)
        => new(value.Ticks - (value.Ticks - EpochTicks) % TimeSpan.TicksPerMicrosecond, value.Kind);

    internal static DateTime? Persisted(DateTime? value) => value is { } at ? Persisted(at) : null;
}
