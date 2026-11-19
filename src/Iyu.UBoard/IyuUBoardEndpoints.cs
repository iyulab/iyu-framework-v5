using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.UBoard;

/// <summary>Puts the parts of the board layer behind routes.</summary>
public static class IyuUBoardEndpoints
{
    /// <summary>The name of the <c>HttpClient</c> the model proxy uses; a host configures its handler like any named client.</summary>
    public const string ModelClientName = "Iyu.UBoard.Model";

    // What an assistant may say to the model service. Everything else a browser sends is dropped: the model is the host's choice,
    // and fields such as a user label or a logit bias are not the page's to set.
    private static readonly string[] Passed =
    [
        "messages", "tools", "tool_choice", "parallel_tool_calls", "stream", "temperature", "top_p", "stop", "response_format",
        "max_tokens", "max_completion_tokens",
    ];

    /// <summary>
    /// Maps the parts that were turned on: with <see cref="IyuUBoardOptions.Model"/>, <c>POST {prefix}/model/chat/completions</c>;
    /// with <see cref="IyuUBoardOptions.Boards"/>, the board routes under <c>{prefix}/boards</c> (make, read, save by revision, rename,
    /// delete, revisions, publish, withdraw, publications, the published revision, the fallback list and a paged list).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The model route takes an OpenAI-style chat-completions body — <c>messages</c>, optional <c>tools</c> and
    /// <c>stream</c> — and sends it to the configured service with the server's key and the configured model. The reply goes back
    /// as it came, a stream as a stream. Only the fields an assistant needs pass; the model name, the key and the caller's own
    /// <c>Authorization</c> header are never taken from the request. A service that refuses the request answers 502 without its
    /// words (they can quote the prompt or the key), a service that is rate-limiting answers 429, one that is too slow answers 504.
    /// </para>
    /// <para>
    /// The routes require a signed-in caller (or <see cref="IyuUBoardOptions.Policy"/>); there is no anonymous mode. A request
    /// must be JSON, within <see cref="IyuUBoardModelOptions.MaxRequestBytes"/>, and same-origin: a browser's
    /// <c>Sec-Fetch-Site</c> other than <c>same-origin</c>, or an <c>Origin</c> that is not this host, is refused with 403, so a
    /// page on another site cannot spend the host's key with the caller's cookie.
    /// </para>
    /// </remarks>
    public static RouteGroupBuilder MapIyuUBoard(this IEndpointRouteBuilder endpoints, string prefix = "/uboard")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<IyuUBoardOptions>>().Value;

        var group = endpoints.MapGroup(prefix);
        if (settings.Policy is { Length: > 0 } policy) group.RequireAuthorization(policy);
        else group.RequireAuthorization();
        group.AddEndpointFilter(IyuUBoardRequestRules.SameOrigin);
        if (settings.ModelEnabled) group.MapPost("model/chat/completions", ChatAsync).AddEndpointFilter(IyuUBoardRequestRules.RequireJson);
        if (settings.BoardsEnabled) IyuUBoardBoardRoutes.Map(group);
        if (settings.CatalogEnabled) IyuUBoardCatalogRoute.Map(group);
        return group;
    }

    private static async Task<IResult> ChatAsync(
        HttpContext http,
        IHttpClientFactory clients,
        IOptions<IyuUBoardOptions> options,
        ILoggerFactory loggers)
    {
        var model = options.Value.ModelSettings;

        var (bytes, refusal) = await ReadBodyAsync(http.Request, model.MaxRequestBytes, http.RequestAborted);
        if (bytes is null) return refusal!;

        JsonObject? input;
        try { input = JsonNode.Parse(bytes) as JsonObject; }
        catch (JsonException) { input = null; }
        if (input is null || input["messages"] is not JsonArray)
            return Results.Problem("The request body must be a chat-completions object with a messages list.", statusCode: StatusCodes.Status400BadRequest);

        var outgoing = new JsonObject();
        foreach (var field in Passed)
            if (input[field] is { } value) outgoing[field] = value.DeepClone();
        // The host's own fields win over the page's, and come before the length is closed: a pinned max_tokens is held to the limit too.
        if (model.ExtraBody is not null)
            foreach (var (field, value) in model.ExtraBody) outgoing[field] = value?.DeepClone();
        outgoing["model"] = model.ModelName;
        if (!LimitOutput(outgoing, model))
            return Results.Problem("max_tokens and max_completion_tokens must be whole numbers greater than zero.", statusCode: StatusCodes.Status400BadRequest);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(model.BaseUrl!.AbsoluteUri.TrimEnd('/') + "/chat/completions"))
        {
            Content = new StringContent(outgoing.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", model.ApiKey);

        var log = loggers.CreateLogger("Iyu.UBoard.Model");
        HttpResponseMessage upstream;
        try
        {
            upstream = await clients.CreateClient(ModelClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("The model service did not answer within the configured time.");
            return Results.Problem("The model service did not answer in time.", statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException failure)
        {
            // The exception type and status are enough to act on; its message can carry the address.
            log.LogWarning("The model service could not be reached ({Status}).", failure.StatusCode?.ToString() ?? failure.HttpRequestError.ToString());
            return Results.Problem("The model service could not be reached.", statusCode: StatusCodes.Status502BadGateway);
        }

        using (upstream)
        {
            if (!upstream.IsSuccessStatusCode)
            {
                log.LogWarning("The model service refused the request with {Status}.", (int)upstream.StatusCode);
                if (upstream.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (upstream.Headers.RetryAfter?.Delta is { } wait)
                        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Problem("The model service is rate-limiting; try again shortly.", statusCode: StatusCodes.Status429TooManyRequests);
                }

                return Results.Problem("The model service refused the request.", statusCode: StatusCodes.Status502BadGateway);
            }

            var response = http.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Accel-Buffering"] = "no"; // a stream is read as it arrives, not when a proxy has seen all of it

            await using var body = await upstream.Content.ReadAsStreamAsync(http.RequestAborted);
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer, http.RequestAborted)) > 0)
                {
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                    await response.Body.FlushAsync(http.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                // The page went away; there is nobody to tell.
            }
        }

        return Results.Empty;
    }

    /// <summary>
    /// Closes the reply's length: a value the page sent is kept only as a whole number from 1 to the limit (a larger one is
    /// lowered, anything that is not a number is refused), and a request that sent none gets the limit — the service's own default
    /// is not the host's to rely on, since the key pays for every token.
    /// </summary>
    private static bool LimitOutput(JsonObject outgoing, IyuUBoardModelOptions model)
    {
        var sent = false;
        foreach (var field in new[] { "max_tokens", "max_completion_tokens" })
        {
            if (!outgoing.ContainsKey(field)) continue;
            sent = true;
            if (outgoing[field] is not JsonValue asked || !asked.TryGetValue<double>(out var tokens) || double.IsNaN(tokens) || tokens < 1)
                return false;
            if (double.IsInfinity(tokens) || tokens > model.MaxOutputTokens) outgoing[field] = model.MaxOutputTokens;
            else if (tokens != Math.Floor(tokens)) return false;
            else outgoing[field] = (int)tokens;
        }

        if (!sent) outgoing[model.OutputLimitField] = model.MaxOutputTokens;
        return true;
    }

    private static async Task<(byte[]? Bytes, IResult? Refusal)> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancel)
    {
        if (request.ContentLength > limit)
            return (null, Results.Problem("The request is too large.", statusCode: StatusCodes.Status413PayloadTooLarge));

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancel)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
                return (null, Results.Problem("The request is too large.", statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        return (buffer.ToArray(), null);
    }
}
