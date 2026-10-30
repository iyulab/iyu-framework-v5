using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lowline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.Lowline;

/// <summary>Puts the forms behind two routes.</summary>
public static class IyuLowlineEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps <c>POST {prefix}/{form}/propose</c> and <c>POST {prefix}/{form}/settle</c> — the same two routes, and the same
    /// request and response bodies, as the engine's own web host, so its form element talks to either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>propose</c> takes <c>{ "id", "values" }</c> — the values the screen holds so far — and answers what to show for
    /// each open decided field. The memory space and the choices the caller may pick come from the form's
    /// <see cref="ILowlineFormHook"/>. <c>settle</c> takes <c>{ "id", "draft" }</c> and answers <c>{ "id", "settledAt" }</c>
    /// once the record is stored and learned; it reads the saved record through the hook and takes <b>no values from the
    /// request</b> — see <see cref="IyuLowlineSettleService"/>.
    /// </para>
    /// <para>
    /// The routes require a signed-in caller (or <see cref="IyuLowlineOptions.Policy"/>); there is no anonymous mode. A
    /// request must be JSON, within <see cref="IyuLowlineOptions.MaxRequestBytes"/>, and same-origin: a browser's
    /// <c>Sec-Fetch-Site</c> other than <c>same-origin</c>, or an <c>Origin</c> that is not this host, is refused with 403,
    /// so a page on another site cannot act with the caller's cookie. A form that is not defined, a record the hook does not
    /// return for the caller and a form with no hook are all 404. A refused settle is 400 without the engine's words, which
    /// can quote a stored value.
    /// </para>
    /// </remarks>
    public static RouteGroupBuilder MapIyuLowline(this IEndpointRouteBuilder endpoints, string prefix = "/lowline")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<IyuLowlineOptions>>().Value;

        var group = endpoints.MapGroup(prefix);
        if (settings.Policy is { } policy) group.RequireAuthorization(policy);
        else group.RequireAuthorization();
        group.AddEndpointFilter(SameOriginJson);
        group.MapPost("{form}/propose", ProposeAsync);
        group.MapPost("{form}/settle", SettleAsync);
        return group;
    }

    private static async ValueTask<object?> SameOriginJson(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var request = invocation.HttpContext.Request;

        // Browsers say where a request came from; anything but this origin is another site acting with the caller's cookie.
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none"))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // A form post (a request another site can send without asking) is not JSON.
        if (!request.HasJsonContentType())
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        return await next(invocation);
    }

    private static async Task<IResult> ProposeAsync(
        string form,
        HttpContext http,
        IEnumerable<ILowlineFormHook> hooks,
        IyuLowlineProposeService service,
        IOptions<IyuLowlineOptions> options)
    {
        var settings = options.Value;
        if (!settings.Engine.Forms.ContainsKey(form) || Hook(hooks, form) is null)
            return Results.NotFound();

        var (request, refusal) = await ReadAsync<ProposeRequest>(http, settings.MaxRequestBytes);
        if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Values is null)
            return refusal ?? Results.Problem("The request body does not read.", statusCode: StatusCodes.Status400BadRequest);

        // The route hands the service what the request carried; a host that derives the values itself calls the same service.
        var outcome = await service.ProposeAsync(form, request.Id, request.Values, http.User, http.RequestAborted);
        switch (outcome.Status)
        {
            case IyuLowlineProposeStatus.Proposed:
                // The engine's types carry their own wire names and enum spellings, so the default serializer writes them as
                // the engine's own host does.
                return Results.Json(outcome.Proposals, Web);

            case IyuLowlineProposeStatus.Refused:
                // The input here is the caller's own, so the engine's words about it go back to them.
                return Results.Problem(outcome.Detail, statusCode: StatusCodes.Status400BadRequest);

            case IyuLowlineProposeStatus.CheckBacklog:
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling((outcome.RetryAfter ?? TimeSpan.Zero).TotalSeconds))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Problem(
                    "Too many records are awaiting settlement. Settle them, or wait for the oldest to lapse.",
                    statusCode: StatusCodes.Status429TooManyRequests);

            default:
                return Results.NotFound();
        }
    }

    private static async Task<IResult> SettleAsync(
        string form,
        HttpContext http,
        IyuLowlineSettleService service,
        IOptions<IyuLowlineOptions> options)
    {
        var settings = options.Value;
        var (request, refusal) = await ReadAsync<SettleRequest>(http, settings.MaxRequestBytes);
        if (request is null || string.IsNullOrWhiteSpace(request.Id))
            return refusal ?? Results.Problem("The request body does not read.", statusCode: StatusCodes.Status400BadRequest);

        var draft = request.Draft is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } kept
            ? kept.GetRawText()
            : null;
        var outcome = await service.SettleAsync(form, request.Id, draft, http.User, http.RequestAborted);

        return outcome.Status switch
        {
            IyuLowlineSettleStatus.Settled => Results.Json(
                new SettleResponse(request.Id, outcome.SettledAt!.Value), Web),
            IyuLowlineSettleStatus.Unauthenticated => Results.Unauthorized(),
            IyuLowlineSettleStatus.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
            IyuLowlineSettleStatus.FormUnknown or IyuLowlineSettleStatus.RecordNotFound => Results.NotFound(),
            _ => Results.Problem("The record was refused.", statusCode: StatusCodes.Status400BadRequest),
        };
    }

    private static ILowlineFormHook? Hook(IEnumerable<ILowlineFormHook> hooks, string form) =>
        hooks.FirstOrDefault(h => string.Equals(h.Form, form, StringComparison.Ordinal));

    /// <summary>The request body, read no further than <paramref name="limit"/> bytes; or why it was not.</summary>
    private static async Task<(T? Body, IResult? Refusal)> ReadAsync<T>(HttpContext http, int limit)
        where T : class
    {
        if (http.Request.ContentLength > limit)
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

        // A body without a declared length (chunked) is cut off at the limit the same way.
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            if (buffer.Length + read > limit)
                return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
            buffer.Write(chunk, 0, read);
        }

        try
        {
            buffer.Position = 0;
            return (await JsonSerializer.DeserializeAsync<T>(buffer, Web, http.RequestAborted), null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private sealed record ProposeRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("values")] Dictionary<string, FieldValue> Values);

    private sealed record SettleRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("draft")] JsonElement? Draft);

    private sealed record SettleResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("settledAt")] DateTimeOffset SettledAt);
}
