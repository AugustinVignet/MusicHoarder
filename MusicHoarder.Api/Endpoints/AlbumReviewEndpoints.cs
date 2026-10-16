using System.Data;
using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;

namespace MusicHoarder.Api.Endpoints;

public static class AlbumReviewEndpoints
{
    public static IEndpointRouteBuilder MapAlbumReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/review/album-groups", Search)
            .WithName("SearchAlbumReviewGroups").WithTags("Album review")
            .WithSummary("Page exact source parents, prioritizing groups with few remaining tracks. Read only.")
            .Produces<AlbumReviewGroupsPage>().Produces<AlbumReviewError>(400).Produces(401);
        app.MapPost("/api/review/album-context", ReadContext)
            .WithName("ReadAlbumReviewContext").WithTags("Album review")
            .WithSummary("Read all evidence for an explicit selection and its owned neighbors. No provider calls or writes.")
            .Produces<AlbumReviewContext>().Produces<AlbumReviewError>(400)
            .Produces<AlbumReviewError>(404).Produces<AlbumReviewError>(413).Produces(401);
        return app;
    }

    internal static async Task<IResult> Search(
        MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct,
        string? from = null, string? query = null, string? status = null,
        int minRemaining = 0, int maxRemaining = int.MaxValue, int offset = 0, int limit = 25)
    {
        if (user.User is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await AlbumReviewContextReader.SearchAsync(
                db, user.UserId, from, query, status, minRemaining, maxRemaining, offset, limit, ct));
        }
        catch (AlbumReviewReadException e) { return Results.Json(e.Error, statusCode: e.StatusCode); }
    }

    internal static async Task<IResult> ReadContext(
        AlbumReviewContextRequest request, MusicHoarderDbContext db,
        ICurrentUserAccessor user, IFileSystem files, CancellationToken ct)
    {
        if (user.User is null) return Results.Unauthorized();
        try
        {
            // One coherent database snapshot; filesystem observations are separately qualified.
            // No SaveChanges, provider, job queue or builder is reachable from this reader.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            var result = await AlbumReviewContextReader.ReadAsync(db, files, user.UserId, request, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(result);
        }
        catch (AlbumReviewReadException e) { return Results.Json(e.Error, statusCode: e.StatusCode); }
    }
}
