using System.Text.Json;
using System.Text.RegularExpressions;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Loop;
using IronProw.Core;
using IronProw.IronHive;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;

namespace BoardChat;

// The assistant's side of the page: one route. The page sends the conversation so far, the board rules it wrote for this round and the
// tools it can run (a board edit is checked and saved by the page, so those tools run there); IronHive.Agent runs the loop over a model
// service reached through IronProw, and the answer is either the assistant's words or the tool calls the page now has to run.
//
// What the page cannot do, because the host holds it: name the model or the key, or lift the output limit. What the route refuses: a request
// from another site, one that is not JSON, one over 1 MiB, an unsigned-in caller, and tool names that are not plain identifiers.
//
// TODO(move-to-library: this route belongs with the viewer assets in an adapter package that carries the IronHive packages, so a host
// switches the assistant on instead of carrying this file. The protocol IronHive's own hosts speak has no request that answers a tool
// the client runs, which is why the route has its own shape.)
public static class Assistant
{
    private const int MaxRequestBytes = 1 << 20;
    private const int MaxTools = 32;
    private static readonly Regex Identifier = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Wire = AIJsonUtilities.DefaultOptions;

    /// <summary>What the page sends. <c>History</c> is what an earlier answer returned, handed back as it came. <c>Final</c> closes a turn the page
    /// ended itself: the results it owes are recorded with the words it said, and the model is not asked again.</summary>
    private sealed record Turn(string? System, List<ChatMessage>? History, string? Message, List<Result>? Results, string? Final, List<Declaration>? Tools);

    private sealed record Declaration(string Name, string? Description, JsonElement Schema);

    private sealed record Result(string CallId, string Output);

    public static void AddAssistant(this IServiceCollection services, IConfigurationSection model) =>
        services.AddIronProw().AddIronHiveOpenAICompatible("model", 10, model["Name"] ?? "model", config =>
        {
            config.BaseUrl = model["BaseUrl"];
            config.ApiKey = model["ApiKey"];
            // The service in front of a model may refuse a client that names none.
            config.Headers = new Dictionary<string, string> { ["User-Agent"] = "iyu-boardchat-sample/0.1" };
        });

    public static void MapAssistant(this WebApplication app) =>
        app.MapPost("/assistant/turn", async (HttpContext http, IChatClient chat, IConfiguration configuration, ILoggerFactory logs, CancellationToken stop) =>
        {
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxRequestBytes;
            Turn? turn;
            try
            {
                turn = await JsonSerializer.DeserializeAsync<Turn>(http.Request.Body, Wire, stop);
            }
            catch (BadHttpRequestException ex)
            {
                return Results.StatusCode(ex.StatusCode);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "the request is not a turn" });
            }
            if (turn is null || (turn.Message is null) == (turn.Results is null) || (turn.Final is not null && turn.Results is null) || turn.Tools is not { Count: <= MaxTools } declared || declared.Any(d => !Identifier.IsMatch(d.Name)))
                return Results.BadRequest(new { error = "a turn is a message or the results of the tools the last answer asked for, with at most 32 tools named in plain words" });

            var options = new AgentOptions
            {
                SystemPrompt = turn.System ?? "",
                Tools = [.. declared.Select(d => AIFunctionFactory.CreateDeclaration(d.Name, d.Description, d.Schema))],
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
                MaxTokens = configuration.GetValue("Model:MaxOutputTokens", 4096),
            };
            var loop = new AgentLoop(chat.AsBuilder().UseFunctionInvocation().Build(), options);
            loop.InitializeHistory(turn.History ?? []);
            try
            {
                if (turn.Results is not null)
                    loop.InitializeHistory([.. loop.History, new ChatMessage(ChatRole.Tool, [.. turn.Results.Select(r => new FunctionResultContent(r.CallId, r.Output))])]);
                if (turn.Final is not null)
                {
                    loop.InitializeHistory([.. loop.History, new ChatMessage(ChatRole.Assistant, turn.Final)]);
                    return Results.Json(new { stop = nameof(TurnStopReason.Completed), text = turn.Final, history = loop.History.Where(m => m.Role != ChatRole.System) }, Wire);
                }
                TurnRecord? record = null;
                await foreach (var chunk in turn.Message is { } message ? loop.RunStreamingAsync(message, stop) : loop.ContinueStreamingAsync(stop))
                    record = chunk.Turn ?? record;
                return Results.Json(new
                {
                    stop = record?.StopReason.ToString(),
                    text = record?.Content,
                    toolCalls = record?.ToolCalls.Select(c => new { id = c.CallId, name = c.ToolName, arguments = c.Arguments }),
                    history = loop.History.Where(m => m.Role != ChatRole.System),
                    usage = record?.Usage,
                }, Wire);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The page is told the kind of failure the agent's classifier found; the details (which may name the service) stay in the host's log.
                logs.CreateLogger("Assistant").LogWarning(ex, "the model could not answer a turn");
                var kind = ex is GuardException ? "guard" : new ErrorRecoveryService(new ErrorRecoveryConfig(), []).AnalyzeException(ex, "turn").Error.Category.ToString().ToLowerInvariant();
                return Results.Json(new { error = kind }, statusCode: kind == "ratelimit" ? StatusCodes.Status429TooManyRequests : StatusCodes.Status502BadGateway);
            }
        })
        .RequireAuthorization()
        .AddEndpointFilter(async (invocation, next) =>
        {
            var request = invocation.HttpContext.Request;
            var site = request.Headers["Sec-Fetch-Site"].ToString();
            var origin = request.Headers.Origin.ToString();
            if ((site.Length > 0 && site is not ("same-origin" or "none")) || (origin.Length > 0 && !string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return request.HasJsonContentType() ? await next(invocation) : Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        });
}
