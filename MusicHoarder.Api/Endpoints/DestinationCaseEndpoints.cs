using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Review.Copy;

namespace MusicHoarder.Api.Endpoints;

public record DestinationCaseReconcileRequest(int[]? SongIds, bool Apply);

/// <summary>
/// Repairs rows whose built destination differs from the file on disk only by letter case (written
/// through a case-insensitive mount into an existing folder of another spelling). Only the row's
/// path changes, and only when the on-disk spelling resolves to an existing file; files are untouched.
/// </summary>
public static class DestinationCaseEndpoints
{
    public static IEndpointRouteBuilder MapDestinationCaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/library/destinations/reconcile-case", Reconcile)
            .WithName("ReconcileDestinationCase")
            .WithSummary("Align built destination paths with the on-disk letter case (dry run unless apply=true).")
            .WithTags("Library").RequireAdmin();
        return app;
    }

    internal static async Task<IResult> Reconcile(DestinationCaseReconcileRequest request, MusicHoarderDbContext db,
        CaseCanonicalDestinationPathResolver resolver, IFileSystem files, CancellationToken ct)
    {
        var query = db.Songs.Where(s => s.DeletedAtUtc == null && s.DestinationPath != null);
        if (request.SongIds is { Length: > 0 } ids) query = query.Where(s => ids.Contains(s.Id));
        await using var guard = request.Apply
            ? await ReviewWriteGuard.AcquireAsync(db, false, ct)
                ?? throw new ReviewPlanException(409, new("writer_busy", "A writer is active. Retry later."))
            : null;
        var songs = await query.ToListAsync(ct);
        var changes = new List<object>();
        foreach (var song in songs)
        {
            var canonical = resolver.Canonicalize(song.DestinationPath!);
            if (canonical == song.DestinationPath || !files.File.Exists(canonical)) continue;
            changes.Add(new { SongId = song.Id, From = song.DestinationPath, To = canonical });
            if (request.Apply) song.DestinationPath = canonical;
        }
        if (request.Apply) await db.SaveChangesAsync(ct);
        return Results.Ok(new { Applied = request.Apply, Examined = songs.Count, Changes = changes });
    }
}
