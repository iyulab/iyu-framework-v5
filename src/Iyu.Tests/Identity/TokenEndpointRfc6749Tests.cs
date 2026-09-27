using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// <c>POST /api/auth/token</c> against the request shapes RFC 6749 §4.4 clients actually send —
/// a form body, or HTTP Basic client authentication — and the §5.2 error shapes they parse.
/// Driven over HTTP, because the difference between the shapes is in how the request is read.
/// </summary>
public class TokenEndpointRfc6749Tests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private HttpClient _http = default!;
    private string _clientId = default!;
    private string _secret = default!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: ["orders.read", "orders.write"]);
        (_clientId, _secret, var hash) = ServiceClientSecrets.Generate();
        store.AddClient(_clientId, hash, owner, perms: ["orders.read", "orders.write"]);

        builder.Services.AddSingleton<IIdentityStore>(store);
        builder.Services.AddSingleton<IServiceClientStore>(store);
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef" },
            permissionCatalog: ["orders.read", "orders.write"]);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapIyuIdentity();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static AuthenticationHeaderValue Basic(string id, string secret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Uri.EscapeDataString(id)}:{Uri.EscapeDataString(secret)}")));

    private async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(
        HttpContent content, AuthenticationHeaderValue? authorization = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/token") { Content = content };
        req.Headers.Authorization = authorization;
        var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task FormBody_IssuesToken_NotCacheable()
    {
        var (res, body) = await PostAsync(Form(("grant_type", "client_credentials"),
            ("client_id", _clientId), ("client_secret", _secret)));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access_token").GetString()));
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.True(res.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task BasicHeader_IssuesToken()
    {
        var (res, body) = await PostAsync(Form(("grant_type", "client_credentials")), Basic(_clientId, _secret));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task BasicHeader_WrongSecret_Is401WithChallenge()
    {
        var (res, body) = await PostAsync(Form(("grant_type", "client_credentials")), Basic(_clientId, "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("Basic", Assert.Single(res.Headers.WwwAuthenticate).Scheme);
        Assert.Equal("invalid_client", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task FormBody_WrongSecret_Is400InvalidClient()
    {
        var (res, body) = await PostAsync(Form(("grant_type", "client_credentials"),
            ("client_id", _clientId), ("client_secret", "wrong")));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_client", body.GetProperty("error").GetString());
        Assert.Empty(res.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task CredentialsInHeaderAndBody_IsInvalidRequest()
    {
        var (res, body) = await PostAsync(Form(("grant_type", "client_credentials"),
            ("client_id", _clientId), ("client_secret", _secret)), Basic(_clientId, _secret));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(null, "invalid_request")]
    [InlineData("password", "unsupported_grant_type")]
    public async Task GrantType_MissingOrOther(string? grantType, string expected)
    {
        var fields = new List<(string, string)> { ("client_id", _clientId), ("client_secret", _secret) };
        if (grantType is not null) fields.Add(("grant_type", grantType));

        var (res, body) = await PostAsync(Form([.. fields]));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(expected, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Scope_Subset_NarrowsTheToken()
    {
        var (res, body) = await PostAsync(Form(("grant_type", "client_credentials"),
            ("client_id", _clientId), ("client_secret", _secret), ("scope", "orders.read")));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("orders.read", body.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Scope_BeyondTheClient_IsInvalidScope()
    {
        var (res, body) = await PostAsync(Form(("grant_type", "client_credentials"),
            ("client_id", _clientId), ("client_secret", _secret), ("scope", "orders.read orders.delete")));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_scope", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task JsonBody_IsStillAccepted()
    {
        var (res, _) = await PostAsync(JsonContent.Create(new
        {
            clientId = _clientId, clientSecret = _secret, grant_type = "client_credentials",
        }));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
