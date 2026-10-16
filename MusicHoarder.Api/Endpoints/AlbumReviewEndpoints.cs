using System.Data;
using Microsoft.AspNetCore.Mvc;
using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Library;
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
        app.MapPost("/api/review/plans/preview", PreviewPlan).RequireAdmin()
            .WithName("PreviewReviewPlan").WithTags("Album review")
            .WithSummary("Persist an exact metadata preview; no track or copy is changed.")
            .Produces<ReviewPlanPreview>().Produces<ReviewPlanError>(400).Produces<ReviewPlanError>(409)
            .Produces<ReviewPlanError>(413).Produces(401).Produces(403);
        app.MapGet("/api/review/plans/{id:guid}", GetPlan).RequireAdmin()
            .WithName("GetReviewPlan").WithTags("Album review").Produces<ReviewPlanPreview>().Produces(404);
        app.MapPost("/api/review/plans/{id:guid}/apply", ApplyPlan).RequireAdmin()
            .WithName("ApplyReviewPlan").WithTags("Album review")
            .WithSummary("Apply only the immutable preview's metadata. Copies await verified build stage.")
            .Produces<ReviewPlanResult>().Produces<ReviewPlanError>(409).Produces(404).Produces(401).Produces(403);
        return app;
    }

    internal static Task<IResult> PreviewPlan(ReviewPlanRequest request, MusicHoarderDbContext db,
        ICurrentUserAccessor user, IFileSystem files, [FromServices] IDestinationPathResolver resolver, CancellationToken ct)
        => PlanResult(user, () => ReviewPlanService.PreviewAsync(db, files, resolver, user.UserId, request, ct));

    internal static Task<IResult> GetPlan(Guid id, MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct)
        => PlanResult(user, () => ReviewPlanService.GetAsync(db, user.UserId, id, ct));

    internal static Task<IResult> ApplyPlan(Guid id, ReviewPlanApplyRequest request, MusicHoarderDbContext db,
        ICurrentUserAccessor user, IFileSystem files, [FromServices] IDestinationPathResolver resolver, CancellationToken ct)
        => PlanResult(user, () => ReviewPlanService.ApplyAsync(db, files, resolver, user.UserId, id, request.Revision, ct));

    private static async Task<IResult> PlanResult<T>(ICurrentUserAccessor user, Func<Task<T>> action)
    {
        if (user.User is null) return Results.Unauthorized();
        if (!user.User.Can(Capability.Administer)) return Results.Forbid();
        try { return Results.Ok(await action()); }
        catch (ReviewPlanException e) { return Results.Json(e.Error, statusCode: e.StatusCode); }
        catch (AlbumReviewReadException e) { return Results.Json(e.Error, statusCode: e.StatusCode); }
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
            // Aspire enables Npgsql retries. The strategy must own the entire transaction,
            // including disposal, so a transient failure restarts a fresh snapshot.
            var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
                var packet = await AlbumReviewContextReader.ReadAsync(db, files, user.UserId, request, token);
                await transaction.CommitAsync(token);
                return packet;
            }, ct);
            return Results.Ok(result);
        }
        catch (AlbumReviewReadException e) { return Results.Json(e.Error, statusCode: e.StatusCode); }
    }
}
