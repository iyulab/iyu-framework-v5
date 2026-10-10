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

    /// <summary>The nonce the handler sent with the challenge, which the ID token must echo.</summary>
    public string Nonce { get; set; } = "";

    /// <summary>Points an OpenID Connect handler at this provider.</summary>
    public void Configure(OpenIdConnectOptions options)
    {
        options.ClientId = ClientId;
        options.ClientSecret = "secret";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.RequireHttpsMetadata = false;
        options.Configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            AuthorizationEndpoint = Issuer + "/authorize",
            TokenEndpoint = Issuer + "/token",
        };
        options.Configuration.SigningKeys.Add(_key);
        options.BackchannelHttpHandler = this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Subject,
                ["nonce"] = Nonce,
                [IyuIdentityClaims.DefaultPermissionClaimType] = ClaimedPermission,
            },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
        var json = $$"""{"access_token":"opaque","token_type":"Bearer","expires_in":300,"id_token":"{{idToken}}"}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
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
