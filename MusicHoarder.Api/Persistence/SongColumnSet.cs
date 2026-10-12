using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// The song columns one read-only pass over the library reads, named once beside the code that
/// reads them (see <see cref="SongMetadataLightQuery.WithOnly"/>).
///
/// <para>
/// It is an include list, so the usual default is inverted: a column that is <b>not</b> named is
/// not read, and comes back as the value a blank <see cref="SongMetadata"/> has (null, false, the
/// property's initializer). Code that starts reading another property of these rows must add it
/// here, or it silently works with that blank.
/// </para>
/// </summary>
public sealed class SongColumnSet
{
    private readonly PropertyInfo[] _properties;
    private Expression<Func<SongMetadata, SongMetadata>>? _projection;

    /// <param name="names">Property names of <see cref="SongMetadata"/>; use <c>nameof</c>.</param>
    public SongColumnSet(params string[] names)
    {
        // Ordinal order, so two sets naming the same columns are the same set however they were written.
        _properties = names
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => typeof(SongMetadata).GetProperty(name) is { SetMethod.IsPublic: true } property
                ? property
                : throw new ArgumentException($"SongMetadata has no settable property '{name}'.", nameof(names)))
            .ToArray();
        Names = _properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlySet<string> Names { get; }

    /// <summary><c>s => new SongMetadata { A = s.A, … }</c> over exactly <see cref="Names"/>.</summary>
    internal Expression<Func<SongMetadata, SongMetadata>> Projection(DbContext db) =>
        LazyInitializer.EnsureInitialized(ref _projection, () => Build(db));

    private Expression<Func<SongMetadata, SongMetadata>> Build(DbContext db)
    {
        var entity = db.Model.FindEntityType(typeof(SongMetadata))
            ?? throw new InvalidOperationException("SongMetadata is not part of the model.");
        // A property the model does not map cannot be selected as a column: EF would fall back to
        // loading the whole row to compute it, which is the very read this exists to avoid.
        if (_properties.FirstOrDefault(p => entity.FindProperty(p.Name) is null) is { } unmapped)
            throw new InvalidOperationException($"SongMetadata.{unmapped.Name} is not a mapped column.");

        var song = Expression.Parameter(typeof(SongMetadata), "s");
        var bindings = _properties
            .Select(p => (MemberBinding)Expression.Bind(p, Expression.Property(song, p)));
        return Expression.Lambda<Func<SongMetadata, SongMetadata>>(
            Expression.MemberInit(Expression.New(typeof(SongMetadata)), bindings), song);
    }
}
