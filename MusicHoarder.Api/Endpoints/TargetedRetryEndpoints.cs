using System.IO.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;
using MusicHoarder.Api.Review.Retry;

namespace MusicHoarder.Api.Endpoints;

internal static class TargetedRetryEndpoints
{
    internal static IEndpointRouteBuilder MapTargetedRetryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/review/retries", Create).RequireAdmin().WithName("CreateTargetedRetry")
            .WithTags("Targeted retry").Produces<TargetedRetryResult>().Produces<ReviewPlanError>(400).Produces(409);
        app.MapGet("/api/review/retries/{id:guid}", Get).RequireAdmin().WithName("GetTargetedRetry")
            .WithTags("Targeted retry").Produces<TargetedRetryResult>().Produces(404);
        app.MapGet("/api/review/failures", Failures).RequireAdmin().WithName("ListPipelineStageFailures")
            .WithTags("Targeted retry").Produces<PipelineFailuresPage>();
        app.MapGet("/api/review/tracks/{songId:int}/stages", Stages).RequireAdmin().WithName("GetPipelineStageStates")
            .WithTags("Targeted retry").Produces<IReadOnlyList<PipelineStageResult>>();
        app.MapGet("/api/review/tracks/{songId:int}/attempts", Attempts).RequireAdmin().WithName("GetPipelineStageAttempts")
            .WithTags("Targeted retry").Produces<IReadOnlyList<PipelineAttemptResult>>();
        app.MapPost("/api/review/retries/resume-automation", Resume).RequireAdmin().WithName("ResumeExplicitPipelineAutomation")
            .WithTags("Targeted retry").Produces<object>().Produces<ReviewPlanError>(409);
        return app;
    }
    private static Task<IResult> Create(TargetedRetryRequest request, MusicHoarderDbContext db, ICurrentUserAccessor user, IFileSystem files, CancellationToken ct)
        => Respond(user, () => TargetedRetryService.CreateAsync(db, files, user.UserId, request, ct));
    private static Task<IResult> Get(Guid id, MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct)
        => Respond(user, () => TargetedRetryService.GetAsync(db, user.UserId, id, ct));
    private static Task<IResult> Failures(MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct,
        string? stage = null, int offset = 0, int limit = 25)
        => Respond(user, () => TargetedRetryService.FailuresAsync(db, user.UserId, stage, offset, limit, ct));
    private static Task<IResult> Stages(int songId, MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct)
        => Respond(user, () => TargetedRetryService.StagesAsync(db, user.UserId, songId, ct));
    private static Task<IResult> Attempts(int songId, MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct,
        string? stage = null, int offset = 0, int limit = 25)
        => Respond(user, () => TargetedRetryService.AttemptsAsync(db, user.UserId, songId, stage, offset, limit, ct));
    private static Task<IResult> Resume(ResumeAutomationRequest request, MusicHoarderDbContext db, ICurrentUserAccessor user, IFileSystem files, CancellationToken ct)
        => Respond(user, async () => { await TargetedRetryService.ResumeAutomationAsync(db, files, user.UserId, request, ct); return new { state = "automationResumed", request.SongIds }; });
    internal static async Task<IResult> Respond<T>(ICurrentUserAccessor user, Func<Task<T>> action)
    {
        if (user.User is null) return Results.Unauthorized();
        if (!user.User.Can(Capability.Administer)) return Results.Forbid();
        try { return Results.Ok(await action()); }
        catch (ReviewPlanException e) { return Results.Json(e.Error, statusCode: e.StatusCode); }
    }
}
