using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.VaultAI;

/// <summary>
/// The server half of embedding the chat widget in a host's own pages: the browser asks the host for the widget's
/// address and a short-lived token, and the host asks the chat service for that token with the key it holds. The key
/// stays on the server; the browser only ever receives the short-lived token.
/// </summary>
public static class VaultAIEmbedExtensions
{
    /// <summary>The named <c>HttpClient</c> the token call uses (its handler follows <c>AllowUntrustedServerCertificate</c>).</summary>
    internal const string HttpClientName = "Iyu.VaultAI.Embed";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maps the routes the widget needs — <c>GET {pattern}/config</c> (where to load the widget from, and which
    /// channel) and <c>POST {pattern}/token</c> (a short-lived token, issued by the chat service with the server's key) —
    /// and, for a page that lets the user pick among several channels, <c>GET {pattern}/channels</c> (the allow list
    /// <c>VaultAI:EmbedChannels</c>) and <c>POST {pattern}/channels/{id}/token</c> (a token for a listed channel; any other
    /// id is <c>404</c> and the chat service is not asked). Held to the module's one authorization rule: <c>VaultAI:AuthorizationPolicy</c> when named, any signed-in user
    /// otherwise, and <c>VaultAI:AllowAnonymous</c> is the only way to open it. Nothing changes for a host that does not
    /// call this. Maps nothing when <c>VaultAI:Url</c> is blank, like the rest of the module.
    /// </summary>
    /// <param name="endpoints">The route builder (for example the <c>WebApplication</c>).</param>
    /// <param name="pattern">The path to mount under. The default does not fall under the report API's path.</param>
    /// <returns>The route group; add conventions to it (rate limiting, for instance) as for any group.</returns>
    public static RouteGroupBuilder MapVaultAIEmbed(this IEndpointRouteBuilder endpoints, string pattern = "/api/vault-ai-embed")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var group = endpoints.MapGroup(pattern);
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<VaultAISettings>>().Value;
        if (string.IsNullOrWhiteSpace(settings.Url))
            return group;

        VaultAIAuthorization.EnsureConfigured(endpoints.ServiceProvider, settings);
        if (settings.AllowAnonymous)
            group.AllowAnonymous();
        else if (settings.AuthorizationPolicy is { Length: > 0 } policy)
            group.RequireAuthorization(policy);
        else
            group.RequireAuthorization();

        EnsureChannelsValid(settings);

        group.MapGet("config", Config);
        group.MapPost("token", IssueToken);
        group.MapGet("channels", Channels);
        group.MapPost("channels/{id}/token", IssueChannelToken);
        return group;
    }

    private static bool Enabled(VaultAISettings s) =>
        !string.IsNullOrWhiteSpace(s.Url) && !string.IsNullOrWhiteSpace(s.Token) && !string.IsNullOrWhiteSpace(s.ChannelId);

    private static bool ChannelsEnabled(VaultAISettings s) =>
        !string.IsNullOrWhiteSpace(s.Url) && !string.IsNullOrWhiteSpace(s.Token) && s.EmbedChannels.Count > 0;

    // The allow list is what keeps a caller from naming any channel and getting a token for it with the server's key, so a
    // list the module cannot read as written fails at start rather than being tidied into something the host did not say.
    private static void EnsureChannelsValid(VaultAISettings s)
    {
        // An id of "." or ".." is not a path segment once it is in the upstream address: the address is normalised and the segment
        // disappears, so the server's key would go to another endpoint of the chat service than the one the id names.
        if (s.ChannelId is { } single && IsDotSegment(single.Trim()))
            throw new InvalidOperationException("VaultAI:ChannelId is a dot segment, which cannot be a channel id.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < s.EmbedChannels.Count; i++)
        {
            var id = s.EmbedChannels[i]?.Id;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException($"VaultAI:EmbedChannels:{i}:Id is blank. Every entry names the chat channel it allows.");
            if (id != id.Trim())
                throw new InvalidOperationException($"VaultAI:EmbedChannels:{i}:Id has leading or trailing whitespace. Write the id exactly as the chat service has it.");
            if (IsDotSegment(id))
                throw new InvalidOperationException($"VaultAI:EmbedChannels:{i}:Id is a dot segment, which cannot be a channel id.");
            if (!seen.Add(id))
                throw new InvalidOperationException($"VaultAI:EmbedChannels lists the channel \"{id}\" twice.");
        }
    }

    private static bool IsDotSegment(string id) => id is "." or "..";

    /// <remarks>
    /// The allow list as the page's channel picker needs it: <c>{enabled, origin, channels: [{id, label}]}</c>. Every entry of the list
    /// is shown to every caller the module's authorization admits - there is no per-channel rule - so the list is for channels the host
    /// is content to show to all of them. <c>label</c> is <c>null</c> for an entry without a name.
    /// </remarks>
    private static IResult Channels(IOptions<VaultAISettings> options)
    {
        var s = options.Value;
        if (!ChannelsEnabled(s))
            return Results.Ok(new { enabled = false, origin = "", channels = Array.Empty<object>() });

        var origin = string.IsNullOrWhiteSpace(s.EmbedOrigin) ? s.Url : s.EmbedOrigin;
        return Results.Ok(new
        {
            enabled = true,
            origin = origin.Trim().TrimEnd('/'),
            channels = s.EmbedChannels.Select(c => new { id = c.Id, label = string.IsNullOrEmpty(c.Label) ? null : c.Label }),
        });
    }

    /// <remarks>
    /// With the folder chatbot engine registered, a signed-in caller also gets <c>bots</c>: <c>[{name, state, indexed, total, inProgress,
    /// failed, notIndexable, skipped}]</c>, one per chatbot (<c>state</c> is a code such as <c>ready</c> or <c>indexing</c>). An anonymous
    /// caller never gets it - the widget still works, and the names of the documents' folders are not for everyone. Nothing is added for a host
    /// that does not register the engine.
    /// </remarks>
    private static async Task<IResult> Config(
        HttpContext http, IOptions<VaultAISettings> options, [Microsoft.AspNetCore.Mvc.FromServices] FolderChatbots.FolderChatbotStatusReader? bots, CancellationToken ct)
    {
        var s = options.Value;
        if (!Enabled(s))
            return Results.Ok(new { enabled = false, origin = "", channelId = "" });

        var origin = string.IsNullOrWhiteSpace(s.EmbedOrigin) ? s.Url : s.EmbedOrigin;
        var channel = s.ChannelId!.Trim();
        if (bots is null || http.User.Identity?.IsAuthenticated != true)
            return Results.Ok(new { enabled = true, origin = origin.Trim().TrimEnd('/'), channelId = channel });

        var statuses = await bots.GetAsync(ct);
        return Results.Ok(new
        {
            enabled = true,
            origin = origin.Trim().TrimEnd('/'),
            channelId = channel,
            bots = statuses.Select(b => new
            {
                name = b.Name, state = b.State, indexed = b.Indexed, total = b.Total,
                inProgress = b.InProgress, failed = b.Failed, notIndexable = b.NotIndexable, skipped = b.Skipped,
            }),
        });
    }

    private static async Task<IResult> IssueToken(
        HttpContext http,
        IOptions<VaultAISettings> options,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        // The route mints a credential, so a page on another site must not be able to send the signed-in user here.
        if (VaultAIAuthorization.IsInitiatedByAnotherSite(http.Request))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Cross-site request refused");

        var s = options.Value;
        if (!Enabled(s))
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Chat embed is not configured",
                detail: "VaultAI:ChannelId is not set.");

        return await RequestToken(http, s, s.ChannelId!.Trim(), httpFactory, loggers, ct);
    }

    private static async Task<IResult> IssueChannelToken(
        string id,
        HttpContext http,
        IOptions<VaultAISettings> options,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (VaultAIAuthorization.IsInitiatedByAnotherSite(http.Request))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Cross-site request refused");

        var s = options.Value;
        if (!ChannelsEnabled(s))
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Chat embed channels are not configured",
                detail: "VaultAI:EmbedChannels or VaultAI:Token is not set.");

        // The allow list is checked before the chat service is asked anything: the caller chooses the id, the server's key pays for it.
        // The same answer for an id that is not listed whatever it is - the list is not something to probe.
        if (!s.EmbedChannels.Any(c => string.Equals(c.Id, id, StringComparison.Ordinal)))
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Channel not found");

        return await RequestToken(http, s, id, httpFactory, loggers, ct);
    }

    private static async Task<IResult> RequestToken(
        HttpContext http, VaultAISettings s, string channelId, IHttpClientFactory httpFactory, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("Iyu.VaultAI.Embed");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{s.Url.Trim().TrimEnd('/')}/api/channels/{Uri.EscapeDataString(channelId)}/token");
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
