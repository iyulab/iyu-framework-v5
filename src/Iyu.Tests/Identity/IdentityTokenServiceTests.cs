using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Iyu.MainServer.Identity;
using Xunit;

namespace Iyu.Tests.Identity;

public class IdentityTokenServiceTests
{
    private static IdentityTokenOptions Opts() => new()
    {
        SigningKey = "0123456789abcdef0123456789abcdef", // >=32 bytes for HS256
        Issuer = "iyu-test", Audience = "iyu-api", Lifetime = TimeSpan.FromHours(1),
    };

    private static (FakeIdentityStore store, string clientId, string secret) SeedClient(
        IEnumerable<string> ownerPerms, IEnumerable<string> clientPerms, bool active = true, DateTimeOffset? expiresAt = null)
    {
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: ownerPerms);
        var (clientId, secret, hash) = ServiceClientSecrets.Generate();
        store.AddClient(clientId, hash, owner, active, expiresAt, clientPerms);
        return (store, clientId, secret);
    }

    [Fact]
    public async Task Issue_ReturnsJwt_WithIntersectedPerms()
    {
        var (store, clientId, secret) = SeedClient(
            ownerPerms: ["orders.read", "orders.write"], clientPerms: ["orders.read", "settlement.write"]);
        var svc = new IdentityTokenService(store, Opts(), TimeProvider.System, new RecordingLogger<IdentityTokenService>());

        var r = await svc.IssueClientCredentialsAsync(clientId, secret, default);

        Assert.True(r.Ok);
        Assert.Equal(new[] { "orders.read" }, r.Permissions);   // settlement.write dropped (owner lacks it)
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(r.AccessToken);
        Assert.Contains(jwt.Claims, c => c.Type == "perm" && c.Value == "orders.read");
        Assert.Single(store.Touched);
    }

    [Fact]
    public async Task Issue_RejectsWrongSecret()
    {
        var (store, clientId, _) = SeedClient(["orders.read"], ["orders.read"]);
        var svc = new IdentityTokenService(store, Opts(), TimeProvider.System, new RecordingLogger<IdentityTokenService>());
        var r = await svc.IssueClientCredentialsAsync(clientId, "wrong", default);
        Assert.False(r.Ok);
        Assert.Equal("invalid_client", r.Error);
    }

    [Fact]
    public async Task Issue_RejectsInactiveOrExpiredClient()
    {
        var (store, clientId, secret) = SeedClient(["orders.read"], ["orders.read"], active: false);
        var svc = new IdentityTokenService(store, Opts(), TimeProvider.System, new RecordingLogger<IdentityTokenService>());
        var r = await svc.IssueClientCredentialsAsync(clientId, secret, default);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Issue_RejectsExpiredClient()
    {
        var pastExpiry = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (store, clientId, secret) = SeedClient(
            ["orders.read"], ["orders.read"], active: true, expiresAt: pastExpiry);
        var svc = new IdentityTokenService(store, Opts(), TimeProvider.System, new RecordingLogger<IdentityTokenService>());
        var r = await svc.IssueClientCredentialsAsync(clientId, secret, default);
        Assert.False(r.Ok);
        Assert.Equal("invalid_client", r.Error);
    }

    [Fact]
    public void IssueUserToken_ReturnsJwt_WithCallerClaims()
    {
        var svc = new IdentityTokenService(new FakeIdentityStore(), Opts(), TimeProvider.System, new RecordingLogger<IdentityTokenService>());
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "user-42"),
            new Claim("perm", "orders.read"),
            new Claim("perm", "orders.write"),
        };

        var r = svc.IssueUserToken(claims);

        Assert.True(r.Ok);
        Assert.Equal(new[] { "orders.read", "orders.write" }, r.Permissions);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(r.AccessToken);
        Assert.Contains(jwt.Claims, c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "user-42");
        Assert.Equal((int)Opts().Lifetime.TotalSeconds, r.ExpiresInSeconds);
    }

    [Fact]
    public void IssueUserToken_HonorsLifetimeOverride()
    {
        var svc = new IdentityTokenService(new FakeIdentityStore(), Opts(), TimeProvider.System, new RecordingLogger<IdentityTokenService>());
        var longLifetime = TimeSpan.FromDays(30);

        var r = svc.IssueUserToken([new Claim(JwtRegisteredClaimNames.Sub, "user-42")], longLifetime);

        Assert.True(r.Ok);
        Assert.Equal((int)longLifetime.TotalSeconds, r.ExpiresInSeconds);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(r.AccessToken);
        Assert.True(jwt.ValidTo > DateTime.UtcNow.AddDays(29));
    }

    [Fact]
    public void IssueUserToken_RejectsNonPositiveLifetimeOverride()
    {
        var svc = new IdentityTokenService(new FakeIdentityStore(), Opts(), TimeProvider.System, new RecordingLogger<IdentityTokenService>());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            svc.IssueUserToken([new Claim(JwtRegisteredClaimNames.Sub, "user-42")], TimeSpan.Zero));
    }

    [Fact]
    public void IssueUserToken_RejectsNullClaims()
    {
        var svc = new IdentityTokenService(new FakeIdentityStore(), Opts(), TimeProvider.System, new RecordingLogger<IdentityTokenService>());
        Assert.Throws<ArgumentNullException>(() => svc.IssueUserToken(null!));
    }

    /// <summary>
    /// Every rejection cause is recorded for the operator, while the caller keeps getting one
    /// undifferentiated answer.
    /// </summary>
    /// <remarks>
    /// Both halves are asserted together on purpose. Logging the cause is only safe while the
    /// response stays identical, and the response staying identical is only tolerable while the
    /// cause is recorded somewhere — separating the two would let either half regress alone.
    /// </remarks>
    [Theory]
    [InlineData("missing", "no such client")]
    [InlineData("revoked", "client is revoked")]
    [InlineData("expired", "client has expired")]
    [InlineData("wrong-secret", "secret does not match")]
    public async Task Rejection_records_its_cause_for_the_operator_without_revealing_it(string scenario, string expected)
    {
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "owner", perms: ["orders.read"]);
        var (clientId, secret, hash) = ServiceClientSecrets.Generate();
        if (scenario != "missing")
            store.AddClient(clientId, hash, owner,
                active: scenario != "revoked",
                expiresAt: scenario == "expired" ? DateTimeOffset.UtcNow.AddDays(-1) : null,
                perms: ["orders.read"]);

        var log = new RecordingLogger<IdentityTokenService>();
        var svc = new IdentityTokenService(store, Opts(), TimeProvider.System, log);

        var r = await svc.IssueClientCredentialsAsync(clientId, scenario == "wrong-secret" ? "nope" : secret, default);

        Assert.False(r.Ok);
        Assert.Equal("invalid_client", r.Error);              // the wire answer never varies
        var recorded = Assert.Single(log.Messages);
        Assert.Contains(expected, recorded, StringComparison.Ordinal);
        Assert.Contains(clientId, recorded, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, recorded, StringComparison.Ordinal);
        Assert.DoesNotContain(hash, recorded, StringComparison.Ordinal);
    }

    /// <summary>A successful issuance records nothing — the log is a rejection channel, not a trace.</summary>
    [Fact]
    public async Task A_successful_issuance_records_no_rejection()
    {
        var (store, clientId, secret) = SeedClient(ownerPerms: ["orders.read"], clientPerms: ["orders.read"]);
        var log = new RecordingLogger<IdentityTokenService>();

        var r = await new IdentityTokenService(store, Opts(), TimeProvider.System, log)
            .IssueClientCredentialsAsync(clientId, secret, default);

        Assert.True(r.Ok);
        Assert.Empty(log.Messages);
    }
}
