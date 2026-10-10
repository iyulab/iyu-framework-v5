using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
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
/// A person's session is assembled by the framework from the application's claims source — the session
/// cookie and an access token alike — rather than by hand in each application's sign-in endpoint.
/// </summary>
/// <remarks>
/// The host is what a consuming application has after <c>AddIyuIdentity</c>: its own sign-in endpoints,
/// which here take the subject from the route because checking a credential is not what is under test.
/// </remarks>
public class UserSessionSignInTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    /// <summary>A source that answers exactly the claims a test gives it — including ones it should not.</summary>
    private sealed class ScriptedClaimsSource : IUserClaimsSource
    {
        public Dictionary<string, Claim[]?> Answers { get; } = new();

        public Task<IReadOnlyCollection<Claim>?> GetClaimsAsync(string subject, CancellationToken ct)
            => Task.FromResult<IReadOnlyCollection<Claim>?>(Answers.GetValueOrDefault(subject));
    }

    private sealed record Host(WebApplication App, HttpClient Http, ScriptedClaimsSource Claims) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Host> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var store = new FakeIdentityStore();
        builder.Services.AddSingleton<IIdentityStore>(store);
        builder.Services.AddSingleton<IServiceClientStore>(store);
        builder.Services.AddSingleton<IRefreshTokenStore>(new FakeRefreshTokenStore());
        var claims = new ScriptedClaimsSource();
        builder.Services.AddSingleton<IUserClaimsSource>(claims);
        builder.Services.AddIyuIdentity(
            new IdentityTokenOptions { SigningKey = Key }, permissionCatalog: ["orders.read", "orders.write"]);
        builder.Services.AddIyuRefreshTokens();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // What a consuming app's sign-in endpoints do once they have checked who the person is.
        app.MapPost("/sign-in/cookie/{subject}", async (string subject, HttpContext http, UserSessionService sessions)
            => await sessions.SignInAsync(http, subject) ? Results.Ok() : Results.Unauthorized()).AllowAnonymous();
        app.MapPost("/sign-in/token/{subject}", async (string subject, UserTokenService tokens, CancellationToken ct) =>
        {
            var pair = await tokens.IssueAsync(subject, ct);
            return pair.Ok ? Results.Ok(new { pair.AccessToken }) : Results.Unauthorized();
        }).AllowAnonymous();

        app.MapGet("/whoami", (ClaimsPrincipal user) => new
        {
            Subject = IyuIdentityClaims.FindSubject(user),
            NameIdentifiers = user.FindAll(ClaimTypes.NameIdentifier).Select(c => c.Value).ToArray(),
            Department = user.FindFirstValue("dept"),
        });
        app.MapGet("/orders", () => "orders").RequireAuthorization("orders.read");
        app.MapPost("/orders", () => "written").RequireAuthorization("orders.write");

        await app.StartAsync();
        return new Host(app, app.GetTestClient(), claims);
    }

    private static string CookiesFrom(HttpResponseMessage response)
        => !response.Headers.TryGetValues("Set-Cookie", out var values)
            ? ""
            : string.Join("; ", values.Select(v => v.Split(';', 2)[0]).Where(pair => !pair.EndsWith('=')));

    private static HttpRequestMessage WithCookies(HttpMethod method, string path, string cookies)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookies.Length > 0) request.Headers.Add("Cookie", cookies);
        return request;
    }

    private static HttpRequestMessage WithBearer(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string[] NameIdentifiers(JsonElement whoami)
        => [.. whoami.GetProperty("nameIdentifiers").EnumerateArray().Select(e => e.GetString()!)];

    private static async Task<string> AccessTokenAsync(Host host, string subject)
    {
        var response = await host.Http.PostAsync($"/sign-in/token/{subject}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    [Fact]
    public async Task The_cookie_session_carries_what_the_source_answers_and_nothing_the_endpoint_built()
    {
        await using var host = await StartAsync();
        host.Claims.Answers["u1"] = [IyuIdentityClaims.Permission("orders.read"), new Claim("dept", "logistics")];

        var signIn = await host.Http.PostAsync("/sign-in/cookie/u1", null);
        var cookies = CookiesFrom(signIn);

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.NotEqual("", cookies);
        // The permission the source granted passes its policy; the one it did not is refused, not unauthenticated.
        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(WithCookies(HttpMethod.Get, "/orders", cookies))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Http.SendAsync(WithCookies(HttpMethod.Post, "/orders", cookies))).StatusCode);

        var who = await (await host.Http.SendAsync(WithCookies(HttpMethod.Get, "/whoami", cookies))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("u1", who.GetProperty("subject").GetString());
        Assert.Equal("logistics", who.GetProperty("department").GetString());
    }

    [Fact]
    public async Task A_subject_the_source_refuses_gets_no_session()
    {
        await using var host = await StartAsync();   // the source knows nobody

        var signIn = await host.Http.PostAsync("/sign-in/cookie/ghost", null);

        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal("", CookiesFrom(signIn));
    }

    /// <summary>
    /// The drift this closes: an application that wrote the identifier by hand could write it one way into the
    /// cookie and another way into the token. The framework writes it, from the subject, for both.
    /// </summary>
    [Fact]
    public async Task A_cookie_and_an_access_token_for_one_person_name_them_the_same_way()
    {
        await using var host = await StartAsync();
        host.Claims.Answers["u1"] = [IyuIdentityClaims.Permission("orders.read")];   // no identifier in the answer

        var cookies = CookiesFrom(await host.Http.PostAsync("/sign-in/cookie/u1", null));
        var token = await AccessTokenAsync(host, "u1");

        var byCookie = await (await host.Http.SendAsync(WithCookies(HttpMethod.Get, "/whoami", cookies))).Content.ReadFromJsonAsync<JsonElement>();
        var byToken = await (await host.Http.SendAsync(WithBearer(HttpMethod.Get, "/whoami", token))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("u1", byCookie.GetProperty("subject").GetString());
        Assert.Equal("u1", byToken.GetProperty("subject").GetString());
        // One identifier each — and readable the way application code already reads it.
        Assert.Equal(new[] { "u1" }, NameIdentifiers(byCookie));
        Assert.Equal(new[] { "u1" }, NameIdentifiers(byToken));
    }

    /// <summary>A source written before the framework wrote the identifier restates it; that is one identifier, not two.</summary>
    [Theory]
    [InlineData("sub")]
    [InlineData(ClaimTypes.NameIdentifier)]
    public async Task An_identifier_the_source_restates_is_not_doubled(string identifierType)
    {
        await using var host = await StartAsync();
        host.Claims.Answers["u1"] = [new Claim(identifierType, "u1"), IyuIdentityClaims.Permission("orders.read")];

        var cookies = CookiesFrom(await host.Http.PostAsync("/sign-in/cookie/u1", null));
        var token = await AccessTokenAsync(host, "u1");

        var byCookie = await (await host.Http.SendAsync(WithCookies(HttpMethod.Get, "/whoami", cookies))).Content.ReadFromJsonAsync<JsonElement>();
        var byToken = await (await host.Http.SendAsync(WithBearer(HttpMethod.Get, "/whoami", token))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "u1" }, NameIdentifiers(byCookie));
        Assert.Equal(new[] { "u1" }, NameIdentifiers(byToken));
    }

    /// <summary>
    /// A source that answers someone else's identifier would open a session for the wrong person. It is a
    /// defect in the source, so it surfaces as one — on either form of session — instead of signing anyone in.
    /// </summary>
    [Theory]
    [InlineData("/sign-in/cookie/u1")]
    [InlineData("/sign-in/token/u1")]
    public async Task An_answer_naming_someone_else_signs_nobody_in(string signInPath)
    {
        await using var host = await StartAsync();
        host.Claims.Answers["u1"] = [new Claim("sub", "u2"), IyuIdentityClaims.Permission("orders.read")];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Http.PostAsync(signInPath, null));

        Assert.Contains("'u1'", error.Message);
        Assert.Contains("'u2'", error.Message);
    }
}
