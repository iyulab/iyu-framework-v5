using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Iyu.UBoard;

/// <summary>
/// The assistant's route, <c>POST {prefix}/boards/{id}/turn</c>: asks the application what the caller may do to the board, vets the request,
/// and hands the turn to the registered <see cref="IBoardAssistant"/>. Mapped only when one is registered.
/// </summary>
internal static partial class IyuUBoardTurnRoute
{
    /// <summary>The largest request a turn may carry, in bytes. A larger one is refused with 413.</summary>
    internal const int MaxRequestBytes = 1 << 20;

    /// <summary>The most tools a turn may declare.</summary>
    internal const int MaxTools = 32;

    private static readonly JsonSerializerOptions Wire = IyuUBoardWire.OmittingNulls;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex Identifier();

    private sealed record Request(string? System, List<JsonElement>? History, string? Message, List<Result>? Results, string? Final, List<Declaration>? Tools);

    private sealed record Declaration(string? Name, string? Description, JsonElement Schema, bool? Edits);

    private sealed record Result(string? CallId, string? Output);

    // The boards group already holds a request that has a body to be JSON (415), so a turn is not asked twice.
    internal static void Map(RouteGroupBuilder one) =>
        one.MapPost("turn", async (string id, HttpRequest request, ClaimsPrincipal user, IBoardAccess access, IyuBoardService boards, IBoardAssistant assistant, ILoggerFactory logs) =>
        {
            var cancellationToken = request.HttpContext.RequestAborted;

            // A person who may not even view the board is told it is not there, before anything they sent is looked at.
            if (!await access.AllowsAsync(user, BoardAction.View, id, cancellationToken)) return Results.NotFound();

            var (body, refusal) = await ReadBodyAsync(request, cancellationToken);
            if (refusal is not null) return refusal;
            var (turn, bad) = Vet(body!);
            if (bad is not null) return Results.BadRequest(new { error = bad });

            // A board that is not saved yet is an empty one its first save would make: whoever may make boards may change it.
            var board = await boards.GetAsync(id, cancellationToken);
            var canEdit = await access.AllowsAsync(user, board is null ? BoardAction.Create : BoardAction.Edit, id, cancellationToken);

            // What the person may not do, the model is not offered. (The save is what refuses an edit; this keeps the assistant from promising one.)
            var tools = turn!.Tools!.Where(t => canEdit || t.Edits == false)
                .Select(t => new BoardToolDeclaration(t.Name!, t.Description, t.Schema, t.Edits ?? true))
                .ToList();

            try
            {
                var answer = await assistant.TakeTurnAsync(
                    new BoardAssistantTurn(
                        user, id, board, canEdit, turn.System, turn.History ?? [], turn.Message,
                        turn.Results?.Select(r => new BoardToolResult(r.CallId!, r.Output!)).ToList(), turn.Final, tools),
                    cancellationToken);

                return Results.Json(new
                {
                    answer.Stop,
                    answer.Text,
                    answer.ToolCalls,
                    answer.History,
                    answer.Usage,
                    AnswerOnly = !canEdit,
                }, Wire);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                // The caller is told the kind of failure; what the service said (it may name the service) stays in the host's log.
                logs.CreateLogger("Iyu.UBoard.Assistant").LogWarning(ex, "the assistant could not answer a turn");
                var kind = ex is BoardAssistantException failed ? failed.Kind : BoardAssistantException.Unknown;
                return Results.Json(
                    new { error = kind },
                    Wire,
                    statusCode: kind switch
                    {
                        BoardAssistantException.RateLimit => StatusCodes.Status429TooManyRequests,
                        BoardAssistantException.BadRequest => StatusCodes.Status400BadRequest,
                        _ => StatusCodes.Status502BadGateway,
                    });
            }
        });

    // The request is counted as it is read: a size the server reports for itself is not the same on every host.
    private static async Task<(byte[]? Body, IResult? Refusal)> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var tooLarge = Results.Problem("The request is too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
        if (request.ContentLength > MaxRequestBytes) return (null, tooLarge);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRequestBytes) return (null, tooLarge);
        }

        return (buffer.ToArray(), null);
    }

    // A turn is a message, or the results of the tools the last answer asked for (which may close the turn with words of the screen's own).
    private static (Request? Turn, string? Refusal) Vet(byte[] body)
    {
        const string NotATurn = "the request is not a turn";
        Request? turn;
        try
        {
            turn = JsonSerializer.Deserialize<Request>(body, Wire);
        }
        catch (JsonException)
        {
            return (null, NotATurn);
        }

        if (turn is null) return (null, NotATurn);
        if ((turn.Message is null) == (turn.Results is null) || (turn.Final is not null && turn.Results is null)
            || turn.Tools is not { Count: <= MaxTools } || turn.Tools.Any(t => t.Name is null || !Identifier().IsMatch(t.Name) || t.Schema.ValueKind != JsonValueKind.Object)
            || turn.Results?.Any(r => r.CallId is null || r.Output is null) == true)
            return (null, "a turn is a message or the results of the tools the last answer asked for, with at most 32 tools named in plain words, each with a schema");

        return (turn, null);
    }
}
