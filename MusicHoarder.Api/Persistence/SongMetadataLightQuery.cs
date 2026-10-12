using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Reads songs without the columns that make a row heavy: the fingerprint and every stored lyrics
/// text, several kilobytes per song between them.
///
/// <para>
/// For read-only passes over the whole library that only compare names and identifiers (the
/// duplicate and split-album detectors behind the Inbox badges). Loading full entities made each
/// of those read tens of megabytes it never looked at, on every page load.
/// </para>
///
/// <para>
/// The projection is built from the EF model rather than written out, so a new column is carried
/// by default and the only list to maintain is <see cref="HeavyColumns"/>. The entities it returns
/// are <b>detached and incomplete</b>: never attach or save one, and do not use this where the
/// caller reads one of the omitted properties (they come back null).
/// </para>
/// </summary>
public static class SongMetadataLightQuery
{
    /// <summary>The properties left out. Add a column here only when it holds unbounded text.</summary>
    public static readonly IReadOnlySet<string> HeavyColumns = new HashSet<string>
    {
        nameof(SongMetadata.Fingerprint),
        nameof(SongMetadata.PlainLyrics),
        nameof(SongMetadata.SyncedLyrics),
        nameof(SongMetadata.TranscribedPlainLyrics),
        nameof(SongMetadata.TranscribedSyncedLyrics),
        nameof(SongMetadata.RomanizedPlainLyrics),
        nameof(SongMetadata.RomanizedSyncedLyrics),
        nameof(SongMetadata.TranslatedPlainLyrics),
        nameof(SongMetadata.TranslatedSyncedLyrics),
        nameof(SongMetadata.LastWrittenTagsJson),
    };

    private static Expression<Func<SongMetadata, SongMetadata>>? _projection;

    /// <summary>
    /// The same rows as <paramref name="query"/>, untracked, with <see cref="HeavyColumns"/> unread.
    /// </summary>
    public static IQueryable<SongMetadata> WithoutHeavyText(this IQueryable<SongMetadata> query, DbContext db) =>
        query.AsNoTracking().Select(LazyInitializer.EnsureInitialized(ref _projection, () => Build(db)));

    internal static Expression<Func<SongMetadata, SongMetadata>> Build(DbContext db)
    {
        var entity = db.Model.FindEntityType(typeof(SongMetadata))
            ?? throw new InvalidOperationException("SongMetadata is not part of the model.");
        var song = Expression.Parameter(typeof(SongMetadata), "s");
        var bindings = entity.GetProperties()
            .Select(p => p.PropertyInfo)
            .Where(p => p is { SetMethod.IsPublic: true } && !HeavyColumns.Contains(p.Name))
            .Select(p => (MemberBinding)Expression.Bind(p!, Expression.Property(song, p!)));
        return Expression.Lambda<Func<SongMetadata, SongMetadata>>(
            Expression.MemberInit(Expression.New(typeof(SongMetadata)), bindings), song);
    }
}
