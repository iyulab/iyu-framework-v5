using System.Security.Claims;
using System.Text;
using System.Text.Json;
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

    internal static void Map(RouteGroupBuilder group, bool assistant)
    {
        var boards = group.MapGroup("boards");
        boards.AddEndpointFilter(RefusedValue);
        boards.AddEndpointFilter(IyuUBoardRequestRules.RequireJsonWhenThereIsABody);

        // Only the boards the caller may view, each with what the caller may do to it. The filter runs after the page is cut, so a
        // page can hold fewer boards than asked for; a caller who sees a short page asks for the next skip.
        boards.MapGet("", async (int? skip, int? take, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
        {
            var listed = new List<object>();
            foreach (var b in await service.ListAsync(skip ?? 0, take ?? 100, cancellationToken))
            {
                if (!await access.AllowsAsync(user, BoardAction.View, b.Id, cancellationToken)) continue;
                listed.Add(new { b.Id, b.Name, Revision = b.CurrentRevision, b.PublishedRevision, Can = await CanAsync(access, user, b.Id, cancellationToken) });
            }

            return Results.Json(listed);
        });

        var one = boards.MapGroup("{id}");

        one.MapPost("", async (string id, string name, HttpRequest request, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service) =>
        {
            var cancellationToken = request.HttpContext.RequestAborted;
            if (!await access.AllowsAsync(user, BoardAction.Create, id, cancellationToken)) return Forbidden();
            var (document, refusal) = await ReadDocumentAsync(request);
            if (refusal is not null) return refusal;
            var made = await service.CreateAsync(id, name, document!, Actor(user), cancellationToken);
            if (!made.Succeeded)
            {
                // The id is taken. Whose board it is stays the owner's business: a caller who may not view it learns only that the id is taken.
                return await access.AllowsAsync(user, BoardAction.View, id, cancellationToken)
                    ? Results.Json(await EnvelopeAsync(made.Board!, access, user, cancellationToken), statusCode: StatusCodes.Status409Conflict)
                    : Results.Problem("A board with that id already exists.", statusCode: StatusCodes.Status409Conflict);
            }

            await access.CreatedAsync(user, id, cancellationToken);
            return Results.Json(await EnvelopeAsync(made.Board!, access, user, cancellationToken), statusCode: StatusCodes.Status201Created);
        });

        one.MapGet("", async (string id, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.View, cancellationToken)
            ?? (await service.GetAsync(id, cancellationToken) is { } board ? Results.Json(await EnvelopeAsync(board, access, user, cancellationToken)) : Results.NotFound()));

        // A save names the revision it worked from. A session that is behind is told so, with the board as it now stands.
        one.MapPut("", async (string id, int expected, HttpRequest request, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service) =>
        {
            var cancellationToken = request.HttpContext.RequestAborted;
            if (await Gate(access, user, id, BoardAction.Edit, cancellationToken) is { } denied) return denied;
            var (document, refusal) = await ReadDocumentAsync(request);
            if (refusal is not null) return refusal;
            var change = await service.SaveAsync(id, document!, expected, Actor(user), cancellationToken);
            return change.Status switch
            {
                BoardChangeStatus.Done => Results.Json(await EnvelopeAsync(change.Board!, access, user, cancellationToken)),
                BoardChangeStatus.Conflict => Results.Json(await EnvelopeAsync(change.Board!, access, user, cancellationToken), statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });

        one.MapPut("name", async (string id, string name, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.Edit, cancellationToken)
            ?? (await service.RenameAsync(id, name, cancellationToken) is { Board: { } board } ? Results.Json(await EnvelopeAsync(board, access, user, cancellationToken)) : Results.NotFound()));

        one.MapDelete("", async (string id, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.Delete, cancellationToken)
            ?? (await service.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound()));

        one.MapGet("revisions", async (string id, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.View, cancellationToken)
            ?? Results.Json((await service.ListRevisionsAsync(id, cancellationToken)).Select(r => new { r.Revision, r.CreatedAt, r.CreatedBy })));

        one.MapGet("revisions/{revision:int}", async (string id, int revision, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.View, cancellationToken)
            ?? (await service.GetRevisionAsync(id, revision, cancellationToken) is { } found
                ? Results.Json(new JsonObject
                {
                    ["revision"] = found.Revision,
                    ["createdAt"] = found.CreatedAt,
                    ["createdBy"] = found.CreatedBy,
                    ["document"] = JsonNode.Parse(found.Document),
                })
                : Results.NotFound()));

        one.MapPost("publish", async (string id, int revision, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.Publish, cancellationToken)
            ?? (await service.PublishAsync(id, revision, Actor(user), cancellationToken) is { } line ? Results.Json(line) : Results.NotFound()));

        one.MapPost("withdraw", async (string id, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.Publish, cancellationToken)
            ?? (await service.WithdrawAsync(id, Actor(user), cancellationToken) is { } line ? Results.Json(line) : Results.NotFound()));

        one.MapGet("publications", async (string id, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.View, cancellationToken)
            ?? Results.Json(await service.ListPublicationsAsync(id, cancellationToken)));

        // What readers see: the revision the board is publishing. Nothing published is an ordinary answer, not an error.
        one.MapGet("published", async (string id, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.View, cancellationToken)
            ?? (await service.GetPublishedAsync(id, cancellationToken) is { } published
                ? Results.Json(new JsonObject { ["revision"] = published.Revision, ["document"] = JsonNode.Parse(published.Document) })
                : Results.NoContent()));

        // What a reader falls back through when the publication in force is one its viewer refuses: the revisions published before it,
        // newest first, each once. The check that says which one the viewer accepts runs where the viewer runs, so the list goes to the page.
        one.MapGet("fallback", async (string id, ClaimsPrincipal user, IBoardAccess access, IyuBoardService service, CancellationToken cancellationToken) =>
            await Gate(access, user, id, BoardAction.View, cancellationToken)
            ?? Results.Json((await service.ListEarlierPublicationsAsync(id, cancellationToken)).Select(found => new JsonObject
            {
                ["revision"] = found.Revision,
                ["document"] = JsonNode.Parse(found.Document),
            })));

        // The assistant's turn: only when the host registered one. With none the route is not there at all.
        if (assistant) IyuUBoardTurnRoute.Map(one);
    }

    /// <summary>
    /// What a screen asks first: whether the caller may make boards at all (to show or hide its "new board" button), whether an assistant is
    /// registered (to show or leave out its chat), whether the source catalog is on (what the assistant may read is described there) and where
    /// the host serves its OData (what the boards read their values from).
    /// </summary>
    internal static void MapAccess(RouteGroupBuilder group, bool assistant, IyuUBoardOptions settings) =>
        group.MapGet("access", async (ClaimsPrincipal user, IBoardAccess access, CancellationToken cancellationToken) =>
            Results.Json(new
            {
                Create = await access.AllowsAsync(user, BoardAction.Create, null, cancellationToken),
                Assistant = assistant,
                Catalog = settings.CatalogEnabled,
                settings.DataRoot,
            }));

    // null when the caller may do it. A caller who may not even view the board is told it is not there, whatever they asked; one who may
    // view it but not do this is told so.
    private static async Task<IResult?> Gate(IBoardAccess access, ClaimsPrincipal user, string id, BoardAction action, CancellationToken cancellationToken)
    {
        if (!await access.AllowsAsync(user, BoardAction.View, id, cancellationToken)) return Results.NotFound();
        return action == BoardAction.View || await access.AllowsAsync(user, action, id, cancellationToken) ? null : Forbidden();
    }

    private static IResult Forbidden() => Results.Problem("You may not do that.", statusCode: StatusCodes.Status403Forbidden);

    // What the caller may do to a board they can view, for a screen to show a read-only board as read-only.
    private static async Task<object> CanAsync(IBoardAccess access, ClaimsPrincipal user, string id, CancellationToken cancellationToken) => new
    {
        Edit = await access.AllowsAsync(user, BoardAction.Edit, id, cancellationToken),
        Publish = await access.AllowsAsync(user, BoardAction.Publish, id, cancellationToken),
        Delete = await access.AllowsAsync(user, BoardAction.Delete, id, cancellationToken),
    };

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
    private static async Task<JsonObject> EnvelopeAsync(BoardRecord board, IBoardAccess access, ClaimsPrincipal user, CancellationToken cancellationToken) => new()
    {
        ["id"] = board.Id,
        ["name"] = board.Name,
        ["revision"] = board.CurrentRevision,
        ["publishedRevision"] = board.PublishedRevision,
        ["document"] = JsonNode.Parse(board.Document),
        ["can"] = JsonSerializer.SerializeToNode(await CanAsync(access, user, board.Id, cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
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
