using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iyu.UBoard;

/// <summary>The board routes: what <see cref="IyuUBoardEndpoints.MapIyuUBoard"/> adds under <c>{prefix}/boards</c> when the board store is on.</summary>
internal static class IyuUBoardBoardRoutes
{
    /// <summary>The largest document a request may carry, in bytes. A larger one is refused with 413.</summary>
    internal const int MaxDocumentBytes = 4 * 1024 * 1024;

    internal static void Map(RouteGroupBuilder group)
    {
        var boards = group.MapGroup("boards");
        boards.AddEndpointFilter(RefusedValue);
        boards.AddEndpointFilter(IyuUBoardRequestRules.RequireJsonWhenThereIsABody);

        boards.MapGet("", async (int? skip, int? take, IyuBoardService service) =>
            Results.Json((await service.ListAsync(skip ?? 0, take ?? 100)).Select(b => new
            {
                b.Id,
                b.Name,
                Revision = b.CurrentRevision,
                b.PublishedRevision,
            })));

        var one = boards.MapGroup("{id}");

        one.MapPost("", async (string id, string name, HttpRequest request, ClaimsPrincipal user, IyuBoardService service) =>
        {
            var (document, refusal) = await ReadDocumentAsync(request);
            if (refusal is not null) return refusal;
            var made = await service.CreateAsync(id, name, document!, Actor(user), request.HttpContext.RequestAborted);
            return Results.Json(Envelope(made.Board!), statusCode: made.Succeeded ? StatusCodes.Status201Created : StatusCodes.Status409Conflict);
        });

        one.MapGet("", async (string id, IyuBoardService service) =>
            await service.GetAsync(id) is { } board ? Results.Json(Envelope(board)) : Results.NotFound());

        // A save names the revision it worked from. A session that is behind is told so, with the board as it now stands.
        one.MapPut("", async (string id, int expected, HttpRequest request, ClaimsPrincipal user, IyuBoardService service) =>
        {
            var (document, refusal) = await ReadDocumentAsync(request);
            if (refusal is not null) return refusal;
            var change = await service.SaveAsync(id, document!, expected, Actor(user), request.HttpContext.RequestAborted);
            return change.Status switch
            {
                BoardChangeStatus.Done => Results.Json(Envelope(change.Board!)),
                BoardChangeStatus.Conflict => Results.Json(Envelope(change.Board!), statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });

        one.MapPut("name", async (string id, string name, IyuBoardService service) =>
            await service.RenameAsync(id, name) is { Board: { } board } ? Results.Json(Envelope(board)) : Results.NotFound());

        one.MapDelete("", async (string id, IyuBoardService service) =>
            await service.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        one.MapGet("revisions", async (string id, IyuBoardService service) =>
            Results.Json((await service.ListRevisionsAsync(id)).Select(r => new { r.Revision, r.CreatedAt, r.CreatedBy })));

        one.MapGet("revisions/{revision:int}", async (string id, int revision, IyuBoardService service) =>
            await service.GetRevisionAsync(id, revision) is { } found
                ? Results.Json(new JsonObject
                {
                    ["revision"] = found.Revision,
                    ["createdAt"] = found.CreatedAt,
                    ["createdBy"] = found.CreatedBy,
                    ["document"] = JsonNode.Parse(found.Document),
                })
                : Results.NotFound());

        one.MapPost("publish", async (string id, int revision, ClaimsPrincipal user, IyuBoardService service) =>
            await service.PublishAsync(id, revision, Actor(user)) is { } line ? Results.Json(line) : Results.NotFound());

        one.MapPost("withdraw", async (string id, ClaimsPrincipal user, IyuBoardService service) =>
            await service.WithdrawAsync(id, Actor(user)) is { } line ? Results.Json(line) : Results.NotFound());

        one.MapGet("publications", async (string id, IyuBoardService service) =>
            Results.Json(await service.ListPublicationsAsync(id)));

        // What readers see: the revision the board is publishing. Nothing published is an ordinary answer, not an error.
        one.MapGet("published", async (string id, IyuBoardService service) =>
            await service.GetPublishedAsync(id) is { } published
                ? Results.Json(new JsonObject { ["revision"] = published.Revision, ["document"] = JsonNode.Parse(published.Document) })
                : Results.NoContent());

        // What a reader falls back through when the publication in force is one its viewer refuses: the revisions published before it,
        // newest first, each once. The check that says which one the viewer accepts runs where the viewer runs, so the list goes to the page.
        one.MapGet("fallback", async (string id, IyuBoardService service) =>
            Results.Json((await service.ListEarlierPublicationsAsync(id)).Select(found => new JsonObject
            {
                ["revision"] = found.Revision,
                ["document"] = JsonNode.Parse(found.Document),
            })));
    }

    // A value the service refuses (a blank id, a document that is not JSON, a page size out of range) is the caller's mistake, not the server's.
    private static async ValueTask<object?> RefusedValue(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        try
        {
            return await next(invocation);
        }
        catch (ArgumentException refused)
        {
            return Results.Problem(refused.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    // Who the history names: the signed-in caller's name, else the subject the identity provider gave.
    private static string Actor(ClaimsPrincipal user) =>
        user.Identity?.Name is { Length: > 0 } name ? name
        : user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";

    // A board as a page reads it: the document is sent as the JSON it is, not as a string of JSON.
    private static JsonObject Envelope(BoardRecord board) => new()
    {
        ["id"] = board.Id,
        ["name"] = board.Name,
        ["revision"] = board.CurrentRevision,
        ["publishedRevision"] = board.PublishedRevision,
        ["document"] = JsonNode.Parse(board.Document),
    };

    private static async Task<(string? Document, IResult? Refusal)> ReadDocumentAsync(HttpRequest request)
    {
        var tooLarge = Results.Problem("The document is too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
        if (request.ContentLength > MaxDocumentBytes) return (null, tooLarge);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxDocumentBytes) return (null, tooLarge);
        }

        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), null);
    }
}
