using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Iyu.MainServer.Identity;

/// <summary>Testable endpoint handlers for the identity runtime (registered by AddIyuIdentity / MapIyuIdentity).</summary>
public static class IdentityEndpointHandlers
{
    /// <summary>
    /// <c>POST /api/auth/token</c> — the OAuth2 client-credentials grant (RFC 6749 §4.4), and the
    /// refresh-token grant (§6) when the app has enabled refresh tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accepts the request in the shapes standard clients send: an
    /// <c>application/x-www-form-urlencoded</c> body with <c>grant_type</c>, <c>client_id</c>,
    /// <c>client_secret</c> and optional <c>scope</c>; or the client credentials in an HTTP Basic
    /// header (§2.3.1, which a server must support) with the other parameters in the form body.
    /// A JSON body with <c>clientId</c>/<c>clientSecret</c>/<c>grant_type</c> is still accepted — it
    /// predates the standard shapes and is not one of them.
    /// </para>
    /// <para>
    /// Errors follow §5.2: <c>{ "error": "…" }</c>. Credentials sent in both the header and the body
    /// are <c>invalid_request</c> (§2.3). A credential that does not authenticate is
    /// <c>invalid_client</c> — 401 with <c>WWW-Authenticate: Basic</c> when it came in the header,
    /// 400 when it came in the body. Every answer is marked <c>Cache-Control: no-store</c> (§5.1).
    /// </para>
    /// <para>
    /// <c>grant_type=refresh_token</c> with <c>refresh_token</c> exchanges a person's refresh token through
    /// <see cref="UserTokenService.RefreshAsync"/> — available when <see cref="IRefreshTokenStore"/> and
    /// <see cref="IUserTokenClaimsSource"/> are registered, <c>unsupported_grant_type</c> otherwise. These
    /// tokens are issued to people, not to registered clients, so no client authentication is read for
    /// this grant. A token that cannot be exchanged, for whatever reason, is <c>invalid_grant</c>.
    /// </para>
    /// </remarks>
    public static async Task<IResult> TokenAsync(HttpContext http, IdentityTokenService tokens, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";

        var (req, viaBasic, readError) = await ReadTokenRequestAsync(http.Request, ct);
        if (readError is not null) return OAuthError(readError, StatusCodes.Status400BadRequest);

        var (result, error) = await IssueAsync(req!, tokens, RefreshTokens(http.RequestServices), ct);
        if (error != InvalidClient) return result;
        if (!viaBasic) return OAuthError(InvalidClient, StatusCodes.Status400BadRequest);
        http.Response.Headers.WWWAuthenticate = "Basic realm=\"iyu\", charset=\"UTF-8\"";
        return OAuthError(InvalidClient, StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Issues a token for an already-read request. Transport decisions — which status a failed
    /// authentication gets, the challenge and cache headers — belong to the HTTP overload; this one
    /// answers a failed authentication with 401 and no challenge.
    /// </summary>
    public static async Task<IResult> TokenAsync(TokenRequest req, IdentityTokenService tokens, CancellationToken ct,
        UserTokenService? users = null)
    {
        var (result, error) = await IssueAsync(req, tokens, users, ct);
        return error == InvalidClient ? OAuthError(InvalidClient, StatusCodes.Status401Unauthorized) : result;
    }

    private const string InvalidClient = "invalid_client";

    /// <summary>The refresh-token service, when the app has registered both of its ports.</summary>
    private static UserTokenService? RefreshTokens(IServiceProvider services) =>
        services.GetService(typeof(IRefreshTokenStore)) is not null && services.GetService(typeof(IUserTokenClaimsSource)) is not null
            ? (UserTokenService?)services.GetService(typeof(UserTokenService))
            : null;

    private static async Task<(IResult Result, string? Error)> IssueAsync(
        TokenRequest req, IdentityTokenService tokens, UserTokenService? users, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Grant_Type))
            return Failure("invalid_request");
        if (req.Grant_Type == "refresh_token")
        {
            if (users is null) return Failure("unsupported_grant_type");
            if (string.IsNullOrWhiteSpace(req.Refresh_Token)) return Failure("invalid_request");
            var pair = await users.RefreshAsync(req.Refresh_Token, ct);
            if (!pair.Ok) return Failure(pair.Error!);
            return (Results.Ok(new RefreshTokenResponse(pair.AccessToken!, "Bearer", pair.ExpiresInSeconds,
                pair.RefreshToken!, string.Join(' ', pair.Permissions))), null);
        }
        if (req.Grant_Type != "client_credentials")
            return Failure("unsupported_grant_type");
        if (string.IsNullOrWhiteSpace(req.ClientId) || string.IsNullOrWhiteSpace(req.ClientSecret))
            return Failure("invalid_request");

        var r = await tokens.IssueClientCredentialsAsync(req.ClientId!, req.ClientSecret!, req.Scope, ct);
        if (!r.Ok) return Failure(r.Error!);
        return (Results.Ok(new TokenResponse(r.AccessToken!, "Bearer", r.ExpiresInSeconds, string.Join(' ', r.Permissions))), null);

        static (IResult, string?) Failure(string error) => (OAuthError(error, StatusCodes.Status400BadRequest), error);
    }

    private static IResult OAuthError(string error, int status) => Results.Json(new { error }, statusCode: status);

    /// <returns>
    /// The request, whether its credentials came in a Basic header, or the §5.2 error code that
    /// makes it unreadable.
    /// </returns>
    private static async Task<(TokenRequest? Request, bool ViaBasic, string? Error)> ReadTokenRequestAsync(
        HttpRequest request, CancellationToken ct)
    {
        TokenRequest body;
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            body = new TokenRequest(Single(form["client_id"]), Single(form["client_secret"]),
                Single(form["grant_type"]), Single(form["scope"]), Single(form["refresh_token"]));
        }
        else if (request.HasJsonContentType())
        {
            try
            {
                body = await request.ReadFromJsonAsync<TokenRequest>(ct) ?? new TokenRequest(null, null, null);
            }
            catch (JsonException)
            {
                return (null, false, "invalid_request");
            }
        }
        else
        {
            body = new TokenRequest(null, null, null);
        }

        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return (body, false, null);

        // §2.3: a client uses one authentication method per request.
        if (body.ClientSecret is not null) return (null, true, "invalid_request");
        if (!TryDecodeBasic(authorization["Basic ".Length..].Trim(), out var id, out var secret))
            return (null, true, "invalid_request");
        if (body.ClientId is not null && body.ClientId != id) return (null, true, "invalid_request");

        return (body with { ClientId = id, ClientSecret = secret }, true, null);
    }

    /// <summary>A parameter sent more than once is not read as either value (§3.1).</summary>
    private static string? Single(StringValues values) => values.Count == 1 ? values[0] : null;

    /// <summary>
    /// §2.3.1: the id and secret are each form-urlencoded, joined with a colon, then base64-encoded.
    /// The colon is found before decoding, so an encoded colon inside either value survives.
    /// </summary>
    private static bool TryDecodeBasic(string encoded, out string id, out string secret)
    {
        id = secret = "";
        byte[] raw;
        try { raw = Convert.FromBase64String(encoded); }
        catch (FormatException) { return false; }

        var pair = Encoding.UTF8.GetString(raw);
        var colon = pair.IndexOf(':');
        if (colon <= 0) return false;
        id = WebUtility.UrlDecode(pair[..colon]);
        secret = WebUtility.UrlDecode(pair[(colon + 1)..]);
        return true;
    }

    /// <remarks>The <c>secret</c> in this response is shown once by design and cannot be recovered — rotate
    /// the client if it is lost. The <c>id</c> can be recovered: <c>GET /api/service-clients</c> lists what
    /// the caller owns, which is where rotate and revoke get their handle when the issuing response is gone.</remarks>
    public static async Task<IResult> CreateServiceClientAsync(
        CreateServiceClientRequest req, Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
    {
        var r = await svc.CreateAsync(ownerUserId, req.DisplayName, req.Permissions ?? Array.Empty<string>(), req.ExpiresAt, ct);
        if (!r.Ok)
            return Results.BadRequest(new { error = r.Error, exceeding = r.Exceeding });
        return Results.Created($"/api/service-clients/{r.Id}",
            new { id = r.Id, clientId = r.ClientId, secret = r.PlaintextSecret });   // secret 1회 반환
    }

    /// <summary>Lists the caller's own service clients, revoked ones included and marked inactive.</summary>
    /// <remarks>
    /// Returns <see cref="ServiceClientSummary"/> rather than the stored client: that type has no
    /// secret material on it at all, so "the hash must not be serialised here" holds by construction
    /// instead of by everyone downstream remembering.
    /// </remarks>
    public static async Task<IResult> ListServiceClientsAsync(
        Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
        => Results.Ok(await svc.ListAsync(ownerUserId, ct));

    public static async Task<IResult> RevokeServiceClientAsync(
        Guid id, Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
    {
        var ok = await svc.RevokeAsync(id, ownerUserId, ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    public static async Task<IResult> RotateServiceClientAsync(
        Guid id, Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
    {
        var r = await svc.RotateAsync(id, ownerUserId, ct);
        return r.Ok ? Results.Ok(new { secret = r.PlaintextSecret }) : Results.NotFound();
    }

    public static async Task<IResult> UpdateServiceClientPermissionsAsync(
        Guid id, UpdateServiceClientPermissionsRequest req, Guid ownerUserId, ServiceClientService svc, CancellationToken ct)
    {
        var r = await svc.UpdatePermissionsAsync(id, ownerUserId, req.Permissions ?? Array.Empty<string>(), ct);
        if (r.Ok) return Results.NoContent();
        if (r.Error == "permissions_exceed_owner")
            return Results.BadRequest(new { error = r.Error, exceeding = r.Exceeding });
        return Results.NotFound();
    }
}
