using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.VaultAi;

/// <summary>
/// The server half of embedding the chat widget in a host's own pages: the browser asks the host for the widget's
/// address and a short-lived token, and the host asks the chat service for that token with the key it holds. The key
/// stays on the server; the browser only ever receives the short-lived token.
/// </summary>
public static class VaultAiEmbedExtensions
{
    /// <summary>The named <c>HttpClient</c> the token call uses (its handler follows <c>AllowUntrustedServerCertificate</c>).</summary>
    internal const string HttpClientName = "Iyu.VaultAi.Embed";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maps the two routes the widget needs — <c>GET {pattern}/config</c> (where to load the widget from, and which
    /// channel) and <c>POST {pattern}/token</c> (a short-lived token, issued by the chat service with the server's key).
    /// Held to the module's one authorization rule: <c>VaultAi:AuthorizationPolicy</c> when named, any signed-in user
    /// otherwise, and <c>VaultAi:AllowAnonymous</c> is the only way to open it. Nothing changes for a host that does not
    /// call this. Maps nothing when <c>VaultAi:Url</c> is blank, like the rest of the module.
    /// </summary>
    /// <param name="endpoints">The route builder (for example the <c>WebApplication</c>).</param>
    /// <param name="pattern">The path to mount under. The default does not fall under the report API's path.</param>
    /// <returns>The route group; add conventions to it (rate limiting, for instance) as for any group.</returns>
    public static RouteGroupBuilder MapVaultAiEmbed(this IEndpointRouteBuilder endpoints, string pattern = "/api/vault-ai-embed")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var group = endpoints.MapGroup(pattern);
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<VaultAiSettings>>().Value;
        if (string.IsNullOrWhiteSpace(settings.Url))
            return group;

        VaultAiAuthorization.EnsureConfigured(endpoints.ServiceProvider, settings);
        if (settings.AllowAnonymous)
            group.AllowAnonymous();
        else if (settings.AuthorizationPolicy is { Length: > 0 } policy)
            group.RequireAuthorization(policy);
        else
            group.RequireAuthorization();

        group.MapGet("config", Config);
        group.MapPost("token", IssueToken);
        return group;
    }

    private static bool Enabled(VaultAiSettings s) =>
        !string.IsNullOrWhiteSpace(s.Url) && !string.IsNullOrWhiteSpace(s.Token) && !string.IsNullOrWhiteSpace(s.ChannelId);

    private static IResult Config(IOptions<VaultAiSettings> options)
    {
        var s = options.Value;
        if (!Enabled(s))
            return Results.Ok(new { enabled = false, origin = "", channelId = "" });

        var origin = string.IsNullOrWhiteSpace(s.EmbedOrigin) ? s.Url : s.EmbedOrigin;
        return Results.Ok(new { enabled = true, origin = origin.Trim().TrimEnd('/'), channelId = s.ChannelId!.Trim() });
    }

    private static async Task<IResult> IssueToken(
        HttpContext http,
        IOptions<VaultAiSettings> options,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        // The route mints a credential, so a page on another site must not be able to send the signed-in user here.
        if (VaultAiAuthorization.IsInitiatedByAnotherSite(http.Request))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Cross-site request refused");

        var s = options.Value;
        if (!Enabled(s))
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Chat embed is not configured",
                detail: "VaultAi:ChannelId is not set.");

        var log = loggers.CreateLogger("Iyu.VaultAi.Embed");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{s.Url.Trim().TrimEnd('/')}/api/channels/{Uri.EscapeDataString(s.ChannelId!.Trim())}/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s.EmbedTokenTimeout > TimeSpan.Zero ? s.EmbedTokenTimeout : DefaultTimeout);

        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Status only: the body is the chat service's, not something to repeat to a browser or to scatter in logs.
                log.LogWarning("The chat service refused to issue an embed token (status {Status}).", (int)response.StatusCode);
                return Failed();
            }

            JsonNode? body;
            try { body = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token)); }
            catch (JsonException) { body = null; }

            if (body?["token"] is not JsonValue value || !value.TryGetValue<string>(out var token) || string.IsNullOrEmpty(token))
            {
                log.LogWarning("The chat service answered the embed token request without a token.");
                return Failed();
            }

            // Only the two fields the widget needs leave the server — not whatever else the answer carried.
            var result = new JsonObject { ["token"] = token };
            if (body["expiresAt"] is { } expiresAt)
                result["expiresAt"] = expiresAt.DeepClone();

            http.Response.Headers.CacheControl = "no-store";
            return Results.Content(result.ToJsonString(), "application/json");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("The chat service did not issue an embed token within {Timeout}.", s.EmbedTokenTimeout);
            return Failed();
        }
        catch (HttpRequestException ex)
        {
            log.LogError(ex, "The chat service could not be reached for an embed token.");
            return Failed();
        }
    }

    // The same answer for every kind of failure: the caller learns that the widget cannot start, not why.
    private static IResult Failed() =>
        Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Chat embed token could not be issued");
}
