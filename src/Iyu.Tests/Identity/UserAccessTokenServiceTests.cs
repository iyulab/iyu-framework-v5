using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Iyu.MainServer.Identity;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// A person's access token without a refresh token: assembled from the claims source like a session cookie and a
/// refreshed token, with <c>sub</c> written from the subject — never from claims the caller put together.
/// </summary>
public class UserAccessTokenServiceTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-11T00:00:00Z"));
    private readonly FakeUserClaimsSource _claims = new();
    private readonly RecordingLogger<UserAccessTokenService> _log = new();
    private readonly IdentityTokenOptions _opts = new()
    {
        SigningKey = "0123456789abcdef0123456789abcdef",
        Lifetime = TimeSpan.FromHours(1),
    };

    private UserAccessTokenService Service(IUserClaimsSource? claims = null) => new(
        new IdentityTokenService(new FakeIdentityStore(), _opts, _clock, new RecordingLogger<IdentityTokenService>()),
        claims ?? _claims, _opts, _log);

    private static JwtSecurityToken Read(TokenResult result) => new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);

    [Fact]
    public async Task Signs_what_the_source_answers_with_sub_from_the_subject()
    {
        _claims.Set("u1", "orders.read", "orders.write");

        var result = await Service().IssueAsync("u1");

        Assert.True(result.Ok);
        Assert.Equal(["orders.read", "orders.write"], result.Permissions);
        var jwt = Read(result);
        Assert.Equal("u1", Assert.Single(jwt.Claims, c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(["orders.read", "orders.write"], jwt.Claims.Where(c => c.Type == "perm").Select(c => c.Value));
    }

    [Fact]
    public async Task A_source_that_answers_no_identifier_still_gets_sub()
    {
        var source = new AnsweringSource([IyuIdentityClaims.Permission("orders.read")]);

        var result = await Service(source).IssueAsync("u1");

        Assert.Equal("u1", Assert.Single(Read(result).Claims, c => c.Type == JwtRegisteredClaimNames.Sub).Value);
    }

    [Fact]
    public async Task A_refused_subject_gets_no_token()
    {
        _claims.Set("gone", null);

        var result = await Service().IssueAsync("gone");

        Assert.False(result.Ok);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Null(result.AccessToken);
        Assert.Contains(_log.Messages, m => m.Contains("gone") && m.Contains("declined"));
    }

    [Fact]
    public async Task A_source_naming_someone_else_fails_the_issue()
    {
        var source = new AnsweringSource([new Claim(JwtRegisteredClaimNames.Sub, "someone-else")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(source).IssueAsync("u1"));
    }

    [Fact]
    public async Task Lifetime_is_the_person_s_unless_the_call_names_one()
    {
        _claims.Set("u1", "orders.read");
        var svc = Service();

        Assert.Equal(3600, (await svc.IssueAsync("u1")).ExpiresInSeconds);          // UserAccessTokenLifetime unset → Lifetime

        _opts.UserAccessTokenLifetime = TimeSpan.FromMinutes(15);
        Assert.Equal(900, (await svc.IssueAsync("u1")).ExpiresInSeconds);

        var offline = await svc.IssueAsync("u1", TimeSpan.FromDays(30));             // a client that works offline
        Assert.Equal((int)TimeSpan.FromDays(30).TotalSeconds, offline.ExpiresInSeconds);
        Assert.Equal(_clock.GetUtcNow().AddDays(30).UtcDateTime, Read(offline).ValidTo);
    }

    [Fact]
    public async Task Rejects_a_non_positive_lifetime_and_a_blank_subject()
    {
        _claims.Set("u1", "orders.read");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service().IssueAsync("u1", TimeSpan.Zero));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service().IssueAsync(" "));
    }

    [Fact]
    public void Nothing_public_signs_a_token_over_claims_the_caller_assembled()
    {
        // A bearer token is not re-read while it lives: one signed over hand-assembled claims would carry whatever
        // permissions the caller wrote, for its whole lifetime. People's tokens go through the claims source.
        var signers = typeof(IdentityTokenService).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                          | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(TokenResult)
                        && m.GetParameters().Any(p => typeof(IEnumerable<Claim>).IsAssignableFrom(p.ParameterType)))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}");

        Assert.Empty(signers);
    }

    /// <summary>A source that answers the same claims for every subject.</summary>
    private sealed class AnsweringSource(IReadOnlyCollection<Claim> claims) : IUserClaimsSource
    {
        public Task<IReadOnlyCollection<Claim>?> GetClaimsAsync(string subject, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<Claim>?>(claims);
    }
}
