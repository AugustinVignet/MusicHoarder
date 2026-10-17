using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Review;

namespace MusicHoarder.Api.Endpoints;

public static class AlbumAuditEndpoints
{
    public static IEndpointRouteBuilder MapAlbumAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/review/album-audit/groups", Groups).RequireAdmin()
            .WithName("ListAlbumAuditGroups").WithTags("Album audit")
            .WithSummary("Enumerate every active owned library unit using revision-bound SQL keyset pagination.")
            .Produces<AlbumAuditInventory>().Produces<AlbumReviewError>(400).Produces<AlbumReviewError>(409).Produces(401).Produces(403);
        app.MapGet("/api/review/album-audit/group-members", Members).RequireAdmin()
            .WithName("ListAlbumAuditGroupMembers").WithTags("Album audit")
            .WithSummary("Continue the bounded ID listing of a large primary navigation unit.")
            .Produces<AlbumAuditMemberPage>().Produces<AlbumReviewError>(400).Produces<AlbumReviewError>(409).Produces(401).Produces(403);
        app.MapPost("/api/review/album-audit/context", Context).RequireAdmin()
            .WithName("ReadAlbumAuditContext").WithTags("Album audit")
            .WithSummary("Read explicit program members, cached reference hypotheses, and bounded owner-scoped dependencies.")
            .Produces<AlbumAuditContext>().Produces<AlbumReviewError>(400).Produces<AlbumReviewError>(404)
            .Produces<AlbumReviewError>(409).Produces<AlbumReviewError>(413).Produces(401).Produces(403);
        return app;
    }

    internal static Task<IResult> Groups(MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct,
        string? query = null, string? status = null, int limit = 25, string? cursor = null, [FromServices] AlbumAuditCursorCodec? codec = null)
        => Read(db, user, () => AlbumAuditReader.InventoryAsync(db, user.UserId, query, status, limit, cursor, ct, codec), ct);
    internal static Task<IResult> Members(MusicHoarderDbContext db, ICurrentUserAccessor user, string cursor, CancellationToken ct, [FromServices] AlbumAuditCursorCodec? codec = null)
        => Read(db, user, () => AlbumAuditReader.MembersAsync(db, user.UserId, cursor, ct, codec), ct);
    internal static Task<IResult> Context(AlbumAuditContextRequest request, MusicHoarderDbContext db,
        ICurrentUserAccessor user, IFileSystem files, CancellationToken ct, [FromServices] AlbumAuditCursorCodec? codec = null)
        => Read(db, user, async () => (await AlbumAuditReader.ReadAsync(db, files, user.UserId, request, ct, codec)).Packet, ct);

    private static async Task<IResult> Read<T>(MusicHoarderDbContext db, ICurrentUserAccessor user, Func<Task<T>> action, CancellationToken ct)
    {
        if (user.User is null) return Results.Unauthorized();
        if (!user.User.Can(Capability.Administer)) return Results.Forbid();
        try
        {
            var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
                var packet = await action();
                await tx.CommitAsync(token);
                return packet;
            }, ct);
            return Results.Ok(result);
        }
        catch (AlbumReviewReadException e) { return Results.Json(e.Error, statusCode: e.StatusCode); }
    }
}
