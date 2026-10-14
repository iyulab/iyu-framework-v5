using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// The bearer scheme accepts the one algorithm the application signs its tokens with — declared, not left to what
/// the key happens to support: another HMAC under the same key is refused, and so is an unsigned token.
/// </summary>
public class BearerSigningAlgorithmTests
{
    // Long enough for every HMAC variant, so a refusal below can only be the algorithm's.
    private static readonly string SigningKey = new('k', 64);

    private sealed record Host(WebApplication App, HttpClient Http) : IAsyncDisposable
    {
        public async Task<HttpStatusCode> StatusAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request);
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Host> StartAsync()
    {
        var accounts = new FakeUserClaimsSource();
        accounts.Set("u1");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserClaimsSource>(accounts);
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = SigningKey }, permissionCatalog: ["orders.read"]);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/whoami", (ClaimsPrincipal user) => IyuIdentityClaims.FindSubject(user) ?? "(none)");
        await app.StartAsync();
        return new Host(app, app.GetTestServer().CreateClient());
    }

    /// <summary>A token with everything the bearer scheme checks right — issuer, audience, lifetime — signed as asked.</summary>
    private static string Token(string? algorithm) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "iyu",
        Audience = "iyu-api",
        Subject = new ClaimsIdentity([new Claim("sub", "u1")]),
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = algorithm is null
            ? null
            : new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), algorithm),
    });

    [Fact]
    public async Task The_application_s_own_token_names_the_algorithm_the_scheme_accepts()
    {
        await using var host = await StartAsync();
        await using var scope = host.App.Services.CreateAsyncScope();
        var issued = await scope.ServiceProvider.GetRequiredService<UserAccessTokenService>().IssueAsync("u1");

        Assert.Equal(SecurityAlgorithms.HmacSha256, new JsonWebToken(issued.AccessToken!).Alg);
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(issued.AccessToken!));
        Assert.Equal(HttpStatusCode.OK, await host.StatusAsync(Token(SecurityAlgorithms.HmacSha256)));
    }

    [Theory]
    [InlineData(SecurityAlgorithms.HmacSha384)]
    [InlineData(SecurityAlgorithms.HmacSha512)]
    public async Task Another_hmac_under_the_same_key_is_refused(string algorithm)
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(Token(algorithm)));
    }

    [Fact]
    public async Task An_unsigned_token_is_refused()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await host.StatusAsync(Token(algorithm: null)));
    }
}
