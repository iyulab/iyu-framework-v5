using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// <c>POST /api/auth/token</c> with <c>grant_type=refresh_token</c> (RFC 6749 §6), driven over HTTP:
/// on only when the app turns refresh tokens on (with its two ports), and the access token it returns is
/// one the bearer scheme accepts.
/// </summary>
public class TokenEndpointRefreshGrantTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private sealed record Host(WebApplication App, HttpClient Http, FakeUserTokenClaimsSource Claims) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    private static Task<Host> StartAsync(bool refreshTokens) => StartAsync(refreshTokens, refreshTokens, refreshTokens);

    private static async Task<Host> StartAsync(bool turnedOn, bool tokenStore, bool claimsSource)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var store = new FakeIdentityStore();
        builder.Services.AddSingleton<IIdentityStore>(store);
        builder.Services.AddSingleton<IServiceClientStore>(store);
        var claims = new FakeUserTokenClaimsSource();
        if (tokenStore) builder.Services.AddSingleton<IRefreshTokenStore>(new FakeRefreshTokenStore());
        if (claimsSource) builder.Services.AddSingleton<IUserTokenClaimsSource>(claims);
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = Key }, permissionCatalog: ["orders.read"]);
        if (turnedOn) builder.Services.AddIyuRefreshTokens();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuIdentity();
        // What a consuming app's own sign-in endpoint does after checking the person's credentials.
        app.MapPost("/sign-in/{subject}", async (string subject, UserTokenService users, CancellationToken ct) =>
        {
            var pair = await users.IssueAsync(subject, ct);
            return pair.Ok ? Results.Ok(new { pair.AccessToken, pair.RefreshToken }) : Results.Unauthorized();
        }).AllowAnonymous();
        app.MapGet("/probe", () => "ok").RequireAuthorization("orders.read");
        await app.StartAsync();
        return new Host(app, app.GetTestClient(), claims);
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostTokenAsync(HttpClient http, HttpContent content)
    {
        var res = await http.PostAsync("/api/auth/token", content);
        var text = await res.Content.ReadAsStringAsync();
        return (res, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static FormUrlEncodedContent Refresh(string token) =>
        new([new("grant_type", "refresh_token"), new("refresh_token", token)]);

    private static async Task<string> SignInAsync(Host host, string subject)
    {
        var res = await host.Http.PostAsync($"/sign-in/{subject}", null);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString()!;
    }

    [Fact]
    public async Task The_grant_is_unsupported_until_the_app_turns_refresh_tokens_on()
    {
        await using var host = await StartAsync(refreshTokens: false);

        var (res, body) = await PostTokenAsync(host.Http, Refresh("anything"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("unsupported_grant_type", body.GetProperty("error").GetString());
    }

    /// <summary>Registering the ports is not the switch — turning the feature on is.</summary>
    [Fact]
    public async Task Registered_ports_alone_leave_the_grant_unsupported()
    {
        await using var host = await StartAsync(turnedOn: false, tokenStore: true, claimsSource: true);

        var (res, body) = await PostTokenAsync(host.Http, Refresh("anything"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("unsupported_grant_type", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// Turned on with a port missing is a configuration mistake, not "refresh tokens off": answering
    /// <c>unsupported_grant_type</c> would hide which registration is missing.
    /// </summary>
    [Theory]
    [InlineData(true, false, nameof(IUserTokenClaimsSource))]
    [InlineData(false, true, nameof(IRefreshTokenStore))]
    public async Task Turning_on_with_one_port_of_two_names_the_missing_one(bool tokenStore, bool claimsSource, string missing)
    {
        await using var host = await StartAsync(turnedOn: true, tokenStore, claimsSource);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PostTokenAsync(host.Http, Refresh("anything")));

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public async Task A_missing_port_does_not_disturb_client_credentials()
    {
        await using var host = await StartAsync(turnedOn: true, tokenStore: true, claimsSource: false);

        var (res, body) = await PostTokenAsync(host.Http,
            new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("client_id", "x"), new("client_secret", "y")]));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_client", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_refresh_returns_a_usable_access_token_and_the_next_refresh_token()
    {
        await using var host = await StartAsync(refreshTokens: true);
        host.Claims.Set("u1", "orders.read");
        var refresh = await SignInAsync(host, "u1");

        var (res, body) = await PostTokenAsync(host.Http, Refresh(refresh));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal("orders.read", body.GetProperty("scope").GetString());
        var next = body.GetProperty("refresh_token").GetString();
        Assert.False(string.IsNullOrEmpty(next));
        Assert.NotEqual(refresh, next);

        using var probe = new HttpRequestMessage(HttpMethod.Get, "/probe");
        probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(probe)).StatusCode);
    }

    [Fact]
    public async Task A_json_body_is_accepted_like_the_form()
    {
        await using var host = await StartAsync(refreshTokens: true);
        host.Claims.Set("u1", "orders.read");
        var refresh = await SignInAsync(host, "u1");

        var (res, body) = await PostTokenAsync(host.Http,
            JsonContent.Create(new { grant_type = "refresh_token", refresh_token = refresh }));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refresh_token").GetString()));
    }

    [Fact]
    public async Task A_replayed_refresh_token_is_invalid_grant()
    {
        await using var host = await StartAsync(refreshTokens: true);
        host.Claims.Set("u1", "orders.read");
        var refresh = await SignInAsync(host, "u1");
        await PostTokenAsync(host.Http, Refresh(refresh));

        var (res, body) = await PostTokenAsync(host.Http, Refresh(refresh));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_missing_refresh_token_is_invalid_request()
    {
        await using var host = await StartAsync(refreshTokens: true);

        var (res, body) = await PostTokenAsync(host.Http, new FormUrlEncodedContent([new("grant_type", "refresh_token")]));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
    }
}
