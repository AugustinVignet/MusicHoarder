using Microsoft.EntityFrameworkCore;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Explicitly submitted review fields, including unchanged values, are evidence for one approval.
/// Old approvals without these audit rows deliberately have no field-level override.
/// </summary>
internal static class ManualReviewMetadata
{
    internal const string Source = "ManualReview";

    internal static SongMetadataChange Record(MusicHoarderDbContext db, SongMetadata song, string field, string? oldValue, string? newValue)
    {
        var change = new SongMetadataChange
        {
            Song = song,
            FieldName = field,
            OldValue = oldValue,
            NewValue = newValue,
            Source = Source,
            Confidence = 1,
            CreatedAtUtc = song.ManuallyApprovedAtUtc!.Value,
            AppliedAtUtc = song.ManuallyApprovedAtUtc,
        };
        db.SongMetadataChanges.Add(change);
        return change;
    }

    internal static async Task LoadAsync(MusicHoarderDbContext db, IReadOnlyList<SongMetadata> songs, CancellationToken ct = default)
    {
        foreach (var song in songs)
            song.ManualReviewValues = new Dictionary<string, string?>();

        var ids = songs.Where(s => s.IsManuallyApproved && s.ManuallyApprovedAtUtc.HasValue).Select(s => s.Id).ToList();
        if (ids.Count == 0) return;

        var changes = await db.SongMetadataChanges.IgnoreQueryFilters().AsNoTracking()
            .Where(c => ids.Contains(c.SongId) && c.Source == Source && c.AppliedAtUtc != null && c.RevertedAtUtc == null)
            .ToListAsync(ct);

        foreach (var song in songs.Where(s => ids.Contains(s.Id)))
        {
            song.ManualReviewValues = changes
                .Where(c => c.SongId == song.Id && c.AppliedAtUtc == song.ManuallyApprovedAtUtc)
                .GroupBy(c => c.FieldName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Id).First().NewValue, StringComparer.Ordinal);
        }
    }

    internal static string? CurrentValue(SongMetadata song, string field) => Review.ReviewFields.Text(song, field);

    internal static bool IsExplicitUnknown(SongMetadata song, string field) =>
        song.HasManualReviewValue(field) && song.ManualReviewValues[field] is null;

    internal static string? EffectiveReleaseDate(SongMetadata song)
    {
        // A provider's more precise date may refine an explicit year, never replace it.
        if (song.HasManualReviewValue(nameof(song.Year))
            && Metadata.ReleaseDateParser.ParseYear(song.ReleaseDate) != song.Year)
            return null;
        return song.ReleaseDate;
    }
}
