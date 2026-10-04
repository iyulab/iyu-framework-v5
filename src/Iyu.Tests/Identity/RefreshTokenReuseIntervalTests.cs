using Iyu.MainServer.Identity;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// <see cref="IdentityTokenOptions.RefreshTokenReuseInterval"/>: a refresh whose response was lost is
/// retried with the token it already spent. Within the interval that retry gets a new pair and the
/// lost pair's refresh token is retired; outside it, or once the lost token has been used after all,
/// the second presentation ends the sign-in as strict rotation does.
/// </summary>
public class RefreshTokenReuseIntervalTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-04T00:00:00Z"));
    private readonly FakeRefreshTokenStore _store = new();
    private readonly FakeUserTokenClaimsSource _claims = new();
    private readonly IdentityTokenOptions _opts = new()
    {
        SigningKey = "0123456789abcdef0123456789abcdef",
        Lifetime = TimeSpan.FromMinutes(15),
        RefreshTokenLifetime = TimeSpan.FromDays(30),
        RefreshTokenReuseInterval = Interval,
    };

    private UserTokenService Service()
    {
        _claims.Set("u1", "orders.read");
        var tokens = new IdentityTokenService(new FakeIdentityStore(), _opts, _clock, new RecordingLogger<IdentityTokenService>());
        return new UserTokenService(tokens, _store, _claims, _opts, _clock, new RecordingLogger<UserTokenService>());
    }

    private int LiveTokens() => _store.Tokens.Count(t => t.UsedAt is null && t.RevokedAt is null);

    [Fact]
    public async Task A_retry_within_the_interval_gets_a_new_pair_and_retires_the_lost_one()
    {
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        var lost = await svc.RefreshAsync(first.RefreshToken!, default);   // the response that never arrived
        _clock.Advance(TimeSpan.FromSeconds(5));

        var retried = await svc.RefreshAsync(first.RefreshToken!, default);

        Assert.True(retried.Ok);
        Assert.NotEqual(lost.RefreshToken, retried.RefreshToken);
        Assert.Equal(1, LiveTokens());
        // The retried pair carries the sign-in on.
        Assert.True((await svc.RefreshAsync(retried.RefreshToken!, default)).Ok);
    }

    /// <summary>
    /// The lost refresh token was retired, not left alive beside the new one: whoever presents it
    /// later is presenting a token that was taken out of use, and the sign-in ends.
    /// </summary>
    [Fact]
    public async Task The_retired_replacement_presented_later_ends_the_sign_in()
    {
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        var lost = await svc.RefreshAsync(first.RefreshToken!, default);
        var retried = await svc.RefreshAsync(first.RefreshToken!, default);

        var stolen = await svc.RefreshAsync(lost.RefreshToken!, default);

        Assert.Equal("invalid_grant", stolen.Error);
        Assert.Equal("invalid_grant", (await svc.RefreshAsync(retried.RefreshToken!, default)).Error);
        Assert.All(_store.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Outside_the_interval_a_second_presentation_ends_the_sign_in()
    {
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        await svc.RefreshAsync(first.RefreshToken!, default);
        _clock.Advance(Interval + TimeSpan.FromSeconds(1));

        var late = await svc.RefreshAsync(first.RefreshToken!, default);

        Assert.Equal("invalid_grant", late.Error);
        Assert.All(_store.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    /// <summary>
    /// The replacement was used: the client did receive it and moved on. Presenting the old token
    /// now, however soon, is a copy — not a retry.
    /// </summary>
    [Fact]
    public async Task Once_the_replacement_has_been_used_the_old_token_is_reuse_even_within_the_interval()
    {
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        var second = await svc.RefreshAsync(first.RefreshToken!, default);
        await svc.RefreshAsync(second.RefreshToken!, default);

        var copy = await svc.RefreshAsync(first.RefreshToken!, default);

        Assert.Equal("invalid_grant", copy.Error);
        Assert.All(_store.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Repeated_retries_keep_exactly_one_live_refresh_token()
    {
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        await svc.RefreshAsync(first.RefreshToken!, default);

        UserTokenPair last = default!;
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            last = await svc.RefreshAsync(first.RefreshToken!, default);
            Assert.True(last.Ok);
            Assert.Equal(1, LiveTokens());
        }
        Assert.True((await svc.RefreshAsync(last.RefreshToken!, default)).Ok);
    }

    /// <summary>Two retries racing for the replacement: one wins, the other is reuse.</summary>
    [Fact]
    public async Task Losing_the_race_for_the_replacement_is_reuse()
    {
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        await svc.RefreshAsync(first.RefreshToken!, default);
        _store.LoseNextMarkRace = true;

        var retried = await svc.RefreshAsync(first.RefreshToken!, default);

        Assert.Equal("invalid_grant", retried.Error);
        Assert.All(_store.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public void A_negative_interval_is_refused_where_it_is_set()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IdentityTokenOptions { RefreshTokenReuseInterval = TimeSpan.FromSeconds(-1) });
    }

    [Fact]
    public async Task Each_token_records_the_one_it_was_issued_for()
    {
        var svc = Service();
        var first = await svc.IssueAsync("u1", default);
        await svc.RefreshAsync(first.RefreshToken!, default);

        var root = _store.Tokens.Single(t => t.ParentId is null);
        var child = _store.Tokens.Single(t => t.ParentId is not null);
        Assert.Equal(root.Id, child.ParentId);
    }
}
