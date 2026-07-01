using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Iyu.Tests.Identity;

public class ServiceClientEndpointHandlerTests
{
    private static (ServiceClientService svc, FakeIdentityStore store, Guid owner) Make()
    {
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: ["orders.read", "orders.write"]);
        return (new ServiceClientService(store, store), store, owner);
    }

    [Fact]
    public async Task Create_Ok_Returns201WithSecret()
    {
        var (svc, _, owner) = Make();
        var res = await IdentityEndpointHandlers.CreateServiceClientAsync(
            new CreateServiceClientRequest("tool", ["orders.read"], null), owner, svc, default);
        Assert.Equal(201, Assert.IsAssignableFrom<IStatusCodeHttpResult>(res).StatusCode);
    }

    [Fact]
    public async Task Create_Exceeding_Returns400()
    {
        var (svc, _, owner) = Make();
        var res = await IdentityEndpointHandlers.CreateServiceClientAsync(
            new CreateServiceClientRequest("tool", ["settlement.write"], null), owner, svc, default);
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(res).StatusCode);
    }

    [Fact]
    public async Task Revoke_ByStranger_Returns404()
    {
        var (svc, store, owner) = Make();
        var stranger = store.AddUser("x", "남", perms: []);
        var created = await svc.CreateAsync(owner, "tool", ["orders.read"], null, default);
        var res = await IdentityEndpointHandlers.RevokeServiceClientAsync(created.Id, stranger, svc, default);
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(res).StatusCode);
    }

    [Fact]
    public async Task Rotate_ByOwner_Returns200()
    {
        var (svc, _, owner) = Make();
        var created = await svc.CreateAsync(owner, "tool", ["orders.read"], null, default);
        var res = await IdentityEndpointHandlers.RotateServiceClientAsync(created.Id, owner, svc, default);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(res).StatusCode);
    }

    [Fact]
    public async Task Rotate_ByStranger_Returns404()
    {
        var (svc, store, owner) = Make();
        var stranger = store.AddUser("x", "남", perms: []);
        var created = await svc.CreateAsync(owner, "tool", ["orders.read"], null, default);
        var res = await IdentityEndpointHandlers.RotateServiceClientAsync(created.Id, stranger, svc, default);
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(res).StatusCode);
    }
}
