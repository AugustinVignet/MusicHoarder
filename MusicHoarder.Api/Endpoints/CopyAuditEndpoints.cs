using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Review.Audit;

namespace MusicHoarder.Api.Endpoints;

public static class CopyAuditEndpoints
{
    public static IEndpointRouteBuilder MapCopyAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/review/copy-audits", Create).RequireAdmin()
            .WithName("CreateCopyAudit").WithTags("Copy audits").WithSummary("Queue a durable technical check of existing copies. No rebuild or musical changes.")
            .Produces<CopyAuditJobResult>(202).Produces<ReviewPlanError>(400).Produces<ReviewPlanError>(409).Produces(401).Produces(403).Produces(404).Produces(413);
        app.MapGet("/api/review/copy-audits/{id:guid}", Get).RequireAdmin()
            .WithName("GetCopyAudit").WithTags("Copy audits").WithSummary("Page dated historical technical observations. GET never checks files or starts work.")
            .Produces<CopyAuditJobResult>().Produces<ReviewPlanError>(400).Produces(401).Produces(403).Produces(404);
        app.MapPost("/api/review/copy-audits/{id:guid}/retry", Retry).RequireAdmin()
            .WithName("RetryCopyAudit").WithTags("Copy audits").WithSummary("Create an idempotent child audit for explicit failed/stale IDs, preserving all earlier receipts.")
            .Produces<CopyAuditJobResult>(202).Produces<ReviewPlanError>(400).Produces<ReviewPlanError>(409).Produces(401).Produces(403).Produces(404);
        app.MapPost("/api/review/copy-audits/{id:guid}/cancel", Cancel).RequireAdmin()
            .WithName("CancelCopyAudit").WithTags("Copy audits").WithSummary("Cooperatively stop pending/running checks; keep completed receipts.")
            .Produces<CopyAuditJobResult>().Produces(401).Produces(403).Produces(404);
        app.MapGet("/api/review/copy-audits/{id:guid}/tracks/{songId:int}/attempts", Attempts).RequireAdmin()
            .WithName("ListCopyAuditAttempts").WithTags("Copy audits").Produces<CopyAuditAttemptsPage>().Produces(400).Produces(401).Produces(403).Produces(404);
        return app;
    }
    internal static Task<IResult> Create(CopyAuditRequest request, MusicHoarderDbContext db, ICurrentUserAccessor user,
        IFileSystem files, CancellationToken ct) => Result(user, () => CopyAuditJobs.CreateAsync(db, files, user.UserId, request, ct), true);
    internal static Task<IResult> Retry(Guid id, CopyAuditRetryRequest request, MusicHoarderDbContext db, ICurrentUserAccessor user,
        IFileSystem files, CancellationToken ct) => Result(user, () => CopyAuditJobs.RetryAsync(db, files, user.UserId, id, request, ct), true);
    internal static Task<IResult> Cancel(Guid id, MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct)
        => Result(user, async () => { await CopyAuditJobs.CancelAsync(db, user.UserId, id, ct); return await CopyAuditJobs.GetAsync(db, user.UserId, id, 0, 25, ct); });
    internal static Task<IResult> Get(Guid id, MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct,
        int afterSongId = 0, int limit = 25) => Result(user, () => Snapshot(db, () => CopyAuditJobs.GetAsync(db, user.UserId, id, afterSongId, limit, ct), ct));
    internal static Task<IResult> Attempts(Guid id, int songId, MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct,
        int afterAttempt = 0, int limit = 25) => Result(user, () => Snapshot(db, () => CopyAuditJobs.AttemptsAsync(db, user.UserId, id, songId, afterAttempt, limit, ct), ct));
    private static Task<T> Snapshot<T>(MusicHoarderDbContext db, Func<Task<T>> read, CancellationToken ct)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
            var result = await read(); await tx.CommitAsync(token); return result;
        }, ct);
    private static async Task<IResult> Result<T>(ICurrentUserAccessor user, Func<Task<T>> action, bool accepted = false)
    {
        if (user.User is null) return Results.Unauthorized();
        if (!user.User.Can(Capability.Administer)) return Results.Forbid();
        try
        {
            var value = await action();
            return accepted && value is CopyAuditJobResult job ? Results.Accepted($"/api/review/copy-audits/{job.Id}", value) : Results.Ok(value);
        }
        catch (ReviewPlanException e) { return Results.Json(e.Error, statusCode: e.StatusCode); }
        catch (AlbumReviewReadException e) { return Results.Json(e.Error, statusCode: e.StatusCode); }
    }
}
