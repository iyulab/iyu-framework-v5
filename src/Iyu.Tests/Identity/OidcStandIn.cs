using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// An OpenID Connect provider stood in for in process: a fixed configuration and a token endpoint that answers the code
/// exchange with an ID token signed by a test key. Everything on the application's side is the real handler pipeline.
/// </summary>
/// <remarks>
/// The ID token names <see cref="Subject"/> and also claims a permission in the application's own permission claim type
/// — what a provider (or a person, where the provider lets people edit their own claims) could assert.
/// </remarks>
internal sealed class OidcStandIn : HttpMessageHandler
{
    public const string Issuer = "https://provider.test";
    public const string ClientId = "app";
    public const string ClaimedPermission = "orders.read";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key" };

    /// <summary>The person's identifier at the provider — the ID token's <c>sub</c>.</summary>
    public string Subject { get; set; } = "provider-subject-1";

    /// <summary>
    /// The provider's own session the sign-in belongs to — the ID token's <c>sid</c>. <c>null</c> for a provider that
    /// names none.
    /// </summary>
    public string? SessionId { get; set; } = "provider-session-1";

    /// <summary>The nonce the handler sent with the challenge, which the ID token must echo.</summary>
    public string Nonce { get; set; } = "";

    /// <summary>The ID token the token endpoint last answered with.</summary>
    public string? LastIdToken { get; private set; }

    /// <summary>Points an OpenID Connect handler at this provider, keeping the tokens it receives (<c>SaveTokens</c>).</summary>
    public void Configure(OpenIdConnectOptions options)
    {
        options.ClientId = ClientId;
        options.ClientSecret = "secret";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.RequireHttpsMetadata = false;
        options.SaveTokens = true;
        options.Configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            AuthorizationEndpoint = Issuer + "/authorize",
            TokenEndpoint = Issuer + "/token",
            EndSessionEndpoint = Issuer + "/end-session",
        };
        options.Configuration.SigningKeys.Add(_key);
        options.BackchannelHttpHandler = this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Subject,
            ["nonce"] = Nonce,
            [IyuIdentityClaims.DefaultPermissionClaimType] = ClaimedPermission,
        };
        if (SessionId is not null) claims["sid"] = SessionId;
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
        LastIdToken = idToken;
        var json = $$"""{"access_token":"opaque","token_type":"Bearer","expires_in":300,"id_token":"{{idToken}}"}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }

    /// <summary>
    /// A logout token as this provider would post it to the application's back-channel logout endpoint: signed with
    /// the provider's key, naming <see cref="SessionId"/>, and — like providers in use — carrying no expiry.
    /// </summary>
    /// <param name="change">Edits the claims before signing: remove one, add one, replace one.</param>
    /// <param name="signedWith">Another key to sign with; the provider's own when omitted.</param>
    /// <param name="unsigned">Leave the token unsigned (<c>alg: none</c>).</param>
    public string LogoutToken(Action<Dictionary<string, object>>? change = null, SecurityKey? signedWith = null, bool unsigned = false)
    {
        var claims = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = new[] { ClientId },
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString(),
            ["events"] = new Dictionary<string, object> { ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>() },
        };
        if (SessionId is not null) claims["sid"] = SessionId;
        change?.Invoke(claims);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = unsigned ? null : new SigningCredentials(signedWith ?? _key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>
    /// Follows a challenge to this provider and back: reads the nonce and state off the redirect, posts the
    /// authorization response to the handler's callback, and returns that response — the handler's redirect onward,
    /// with the cookies it set.
    /// </summary>
    /// <param name="http">A client of the application.</param>
    /// <param name="challenge">The application's response that redirects to the provider.</param>
    public async Task<HttpResponseMessage> AnswerAsync(HttpClient http, HttpResponseMessage challenge)
    {
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.StartsWith(Issuer + "/authorize", challenge.Headers.Location!.OriginalString);
        var query = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        Nonce = query["nonce"]!;
        return await http.SendAsync(With(HttpMethod.Post, "/signin-oidc", CookiesFrom(challenge),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "code", ["state"] = query["state"]! })));
    }

    /// <summary>The cookies a response sets, as a request <c>Cookie</c> header value (deletions left out).</summary>
    public static string CookiesFrom(HttpResponseMessage response)
        => !response.Headers.TryGetValues("Set-Cookie", out var values)
            ? ""
            : string.Join("; ", values
                .Select(v => v.Split(';', 2)[0])
                .Where(pair => !pair.EndsWith('=')));

    /// <summary>The cookie names a response deletes (an empty value with an expiry in the past).</summary>
    public static IReadOnlyList<string> CookiesDeletedBy(HttpResponseMessage response)
        => !response.Headers.TryGetValues("Set-Cookie", out var values)
            ? []
            : values.Select(v => v.Split(';', 2)[0]).Where(pair => pair.EndsWith('=')).Select(pair => pair.TrimEnd('=')).ToList();

    public static HttpRequestMessage With(HttpMethod method, string path, string cookies, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (cookies.Length > 0) request.Headers.Add("Cookie", cookies);
        return request;
    }
}
