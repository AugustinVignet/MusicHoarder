using System.IO.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review.Copy;

namespace MusicHoarder.Api.Endpoints;

internal static class ReviewDestinationReleaseEndpoints
{
    internal static IEndpointRouteBuilder MapReviewDestinationReleaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/review/destination-reservations/release", Release).RequireAdmin()
            .WithName("ReleaseReviewDestinationReservation").WithTags("Album review")
            .WithSummary("Release a destination only finished review copies still reserve. Refused while a file, an active song or an unfinished job uses it; released jobs can no longer be rolled back.")
            .Produces<ReviewDestinationReleaseResult>().Produces<ReviewPlanError>(400).Produces<ReviewPlanError>(404).Produces<ReviewPlanError>(409).Produces(401).Produces(403);
        app.MapGet("/api/review/destination-reservations/releases", List).RequireAdmin()
            .WithName("ListReviewDestinationReleases").WithTags("Album review")
            .Produces<IReadOnlyList<ReviewDestinationReleaseResult>>().Produces(401).Produces(403);
        return app;
    }

    internal static Task<IResult> Release(ReviewDestinationReleaseRequest request, MusicHoarderDbContext db, ICurrentUserAccessor user,
        IFileSystem files, CancellationToken ct)
        => TargetedRetryEndpoints.Respond(user, () => ReviewDestinationReleases.ReleaseAsync(db, files, user.UserId, request, ct));

    internal static Task<IResult> List(MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct)
        => TargetedRetryEndpoints.Respond(user, () => ReviewDestinationReleases.ListAsync(db, user.UserId, ct));
}
