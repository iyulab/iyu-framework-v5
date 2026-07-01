using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Iyu.Tests.Identity;

public class TokenEndpointHandlerTests
{
    private static IdentityTokenService Svc(out FakeIdentityStore store, out string clientId, out string secret)
    {
        store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: ["orders.read"]);
        (clientId, secret, var hash) = ServiceClientSecrets.Generate();
        store.AddClient(clientId, hash, owner, perms: ["orders.read"]);
        return new IdentityTokenService(store, new IdentityTokenOptions
        { SigningKey = "0123456789abcdef0123456789abcdef" }, TimeProvider.System);
    }

    [Fact]
    public async Task Token_WrongGrant_Returns400()
    {
        var svc = Svc(out _, out var clientId, out var secret);
        var res = await IdentityEndpointHandlers.TokenAsync(
            new TokenRequest(clientId, secret, "password"), svc, default);
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(res).StatusCode);
    }

    [Fact]
    public async Task Token_ValidClientCredentials_ReturnsOkWithAccessToken()
    {
        var svc = Svc(out _, out var clientId, out var secret);
        var res = await IdentityEndpointHandlers.TokenAsync(
            new TokenRequest(clientId, secret, "client_credentials"), svc, default);
        var ok = Assert.IsType<Ok<TokenResponse>>(res);
        Assert.False(string.IsNullOrWhiteSpace(ok.Value!.access_token));
        Assert.Equal("Bearer", ok.Value.token_type);
    }

    [Fact]
    public async Task Token_BadSecret_Returns401()
    {
        var svc = Svc(out _, out var clientId, out _);
        var res = await IdentityEndpointHandlers.TokenAsync(
            new TokenRequest(clientId, "wrong", "client_credentials"), svc, default);
        Assert.Equal(401, Assert.IsAssignableFrom<IStatusCodeHttpResult>(res).StatusCode);
    }
}
