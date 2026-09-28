using System.IdentityModel.Tokens.Jwt;
using Iyu.MainServer.Identity;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// Rotating refresh tokens for people: each is accepted once, a second presentation ends the whole
/// sign-in, and every refresh asks the app again who the person is.
/// </summary>
public class UserTokenServiceTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
    private readonly FakeRefreshTokenStore _store = new();
    private readonly FakeUserTokenClaimsSource _claims = new();
    private readonly IdentityTokenOptions _opts = new()
    {
        SigningKey = "0123456789abcdef0123456789abcdef",
        Lifetime = TimeSpan.FromMinutes(15),
        RefreshTokenLifetime = TimeSpan.FromDays(30),
    };

    private UserTokenService Service()
    {
        var tokens = new IdentityTokenService(new FakeIdentityStore(), _opts, _clock, new RecordingLogger<IdentityTokenService>());
        return new UserTokenService(tokens, _store, _claims, _opts, _clock, new RecordingLogger<UserTokenService>());
    }

    private static string[] Perms(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Where(c => c.Type == "perm").Select(c => c.Value).ToArray();

    [Fact]
    public async Task Issue_signs_the_claims_the_source_gives_and_a_refresh_token()
    {
        _claims.Set("u1", "orders.read");

        var pair = await Service().IssueAsync("u1", default);

        Assert.True(pair.Ok);
        Assert.Equal(["orders.read"], Perms(pair.AccessToken!));
        Assert.Equal(900, pair.ExpiresInSeconds);
        Assert.False(string.IsNullOrEmpty(pair.RefreshToken));
        var stored = Assert.Single(_store.Tokens);
        Assert.Equal("u1", stored.Subject);
        Assert.Equal(_clock.GetUtcNow().AddDays(30), stored.ExpiresAt);
    }

    [Fact]
    public async Task A_person_s_access_lifetime_is_set_apart_from_service_client_tokens()
    {
        // Shortening a person's access token must not shorten every service client's.
        _opts.Lifetime = TimeSpan.FromHours(1);
        _opts.UserAccessTokenLifetime = TimeSpan.FromMinutes(15);
        _claims.Set("u1", "orders.read");
        var svc = Service();

        var first = await svc.IssueAsync("u1", default);
        var refreshed = await svc.RefreshAsync(first.RefreshToken!, default);

        Assert.Equal(900, first.ExpiresInSeconds);
        Assert.Equal(900, refreshed.ExpiresInSeconds);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(15).UtcDateTime,
            new JwtSecurityTokenHandler().ReadJwtToken(refreshed.AccessToken).ValidTo, TimeSpan.FromSeconds(1));

        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: ["orders.read"]);
        var (clientId, secret, hash) = ServiceClientSecrets.Generate();
        store.AddClient(clientId, hash, owner, perms: ["orders.read"]);
        var client = await new IdentityTokenService(store, _opts, _clock, new RecordingLogger<IdentityTokenService>())
            .IssueClientCredentialsAsync(clientId, secret, default);
        Assert.Equal(3600, client.ExpiresInSeconds);
    }

    [Fact]
    public async Task Without_its_own_lifetime_a_person_s_access_token_uses_Lifetime()
    {
        _opts.Lifetime = TimeSpan.FromMinutes(20);
        _claims.Set("u1", "orders.read");

        var pair = await Service().IssueAsync("u1", default);

        Assert.Equal(1200, pair.ExpiresInSeconds);
    }

    [Fact]
    public async Task Only_the_hash_of_a_refresh_token_is_stored()
    {
        _claims.Set("u1", "orders.read");
        var pair = await Service().IssueAsync("u1", default);

        var stored = Assert.Single(_store.Tokens);
        Assert.NotEqual(pair.RefreshToken, stored.TokenHash);
        Assert.Equal(UserTokenService.HashRefreshToken(pair.RefreshToken!), stored.TokenHash);
    }

    [Fact]
    public async Task Issue_is_refused_when_the_source_declines_the_subject()
    {
        var pair = await Service().IssueAsync("nobody", default);

        Assert.False(pair.Ok);
        Assert.Equal("invalid_grant", pair.Error);
        Assert.Empty(_store.Tokens);
    }

    [Fact]
    public async Task Refresh_rotates_and_the_new_token_is_the_one_that_works()
    {
        _claims.Set("u1", "orders.read");
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);

        var second = await svc.RefreshAsync(first.RefreshToken!, default);
        var third = await svc.RefreshAsync(second.RefreshToken!, default);

        Assert.True(second.Ok);
        Assert.True(third.Ok);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Single(_store.Tokens.Select(t => t.FamilyId).Distinct());
    }

    [Fact]
    public async Task A_used_token_presented_again_ends_the_whole_sign_in()
    {
        _claims.Set("u1", "orders.read");
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        var second = await svc.RefreshAsync(first.RefreshToken!, default);

        var replay = await svc.RefreshAsync(first.RefreshToken!, default);
        var afterReplay = await svc.RefreshAsync(second.RefreshToken!, default);

        Assert.Equal("invalid_grant", replay.Error);
        Assert.Equal("invalid_grant", afterReplay.Error);   // the legitimate-looking one is gone too
        Assert.All(_store.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Losing_the_race_for_a_token_is_treated_as_reuse()
    {
        _claims.Set("u1", "orders.read");
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        _store.LoseNextMarkRace = true;

        var pair = await svc.RefreshAsync(first.RefreshToken!, default);

        Assert.Equal("invalid_grant", pair.Error);
        Assert.All(_store.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_not_spent()
    {
        _claims.Set("u1", "orders.read");
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        _clock.Advance(TimeSpan.FromDays(30));

        var pair = await svc.RefreshAsync(first.RefreshToken!, default);

        Assert.Equal("invalid_grant", pair.Error);
        var stored = Assert.Single(_store.Tokens);
        Assert.Null(stored.UsedAt);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task Each_refresh_extends_the_sign_in_by_the_full_lifetime()
    {
        _claims.Set("u1", "orders.read");
        var svc = Service();
        var token = (await svc.IssueAsync("u1", default)).RefreshToken!;

        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromDays(20));
            var pair = await svc.RefreshAsync(token, default);
            Assert.True(pair.Ok);
            token = pair.RefreshToken!;
        }
    }

    [Fact]
    public async Task A_narrowed_permission_set_reaches_the_next_access_token()
    {
        _claims.Set("u1", "orders.read", "orders.write");
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        _claims.Set("u1", "orders.read");

        var second = await svc.RefreshAsync(first.RefreshToken!, default);

        Assert.Equal(["orders.read", "orders.write"], Perms(first.AccessToken!));
        Assert.Equal(["orders.read"], Perms(second.AccessToken!));
    }

    [Fact]
    public async Task A_deactivated_person_cannot_refresh_and_the_sign_in_ends()
    {
        _claims.Set("u1", "orders.read");
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        _claims.Set("u1", null);

        var pair = await svc.RefreshAsync(first.RefreshToken!, default);
        _claims.Set("u1", "orders.read");   // reactivated: the old sign-in still does not come back
        var again = await svc.RefreshAsync(first.RefreshToken!, default);

        Assert.Equal("invalid_grant", pair.Error);
        Assert.Equal("invalid_grant", again.Error);
        Assert.All(_store.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Revoke_ends_one_sign_in_and_leaves_the_others()
    {
        _claims.Set("u1", "orders.read");
        var svc = Service();
        var phone = await svc.IssueAsync("u1", default);
        var tablet = await svc.IssueAsync("u1", default);

        await svc.RevokeAsync(phone.RefreshToken!, default);

        Assert.Equal("invalid_grant", (await svc.RefreshAsync(phone.RefreshToken!, default)).Error);
        Assert.True((await svc.RefreshAsync(tablet.RefreshToken!, default)).Ok);
    }

    [Fact]
    public async Task RevokeAll_ends_every_sign_in_of_the_subject_only()
    {
        _claims.Set("u1", "orders.read");
        _claims.Set("u2", "orders.read");
        var svc = Service();
        var phone = await svc.IssueAsync("u1", default);
        var tablet = await svc.IssueAsync("u1", default);
        var other = await svc.IssueAsync("u2", default);

        await svc.RevokeAllAsync("u1", default);

        Assert.False((await svc.RefreshAsync(phone.RefreshToken!, default)).Ok);
        Assert.False((await svc.RefreshAsync(tablet.RefreshToken!, default)).Ok);
        Assert.True((await svc.RefreshAsync(other.RefreshToken!, default)).Ok);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("")]
    public async Task An_unknown_token_is_invalid_grant(string token)
    {
        var pair = await Service().RefreshAsync(token, default);
        Assert.Equal("invalid_grant", pair.Error);
    }
}
