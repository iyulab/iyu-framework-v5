using Iyu.MainServer.Identity;
using Xunit;

namespace Iyu.Tests.Identity;

public class FakeIdentityStoreTests
{
    [Fact]
    public async Task Returns_SeededUser_AndPermissions()
    {
        var store = new FakeIdentityStore();
        var uid = store.AddUser("admin", "관리자", perms: ["orders.read", "orders.write"]);

        var user = await store.FindUserByUsernameAsync("admin", default);
        Assert.NotNull(user);
        Assert.Equal(uid, user!.Id);
        var perms = await store.GetUserPermissionsAsync(uid, default);
        Assert.Contains("orders.read", perms);
        Assert.Equal(2, perms.Count);
    }

    [Fact]
    public async Task Unknown_ClientId_ReturnsNull()
    {
        var store = new FakeIdentityStore();
        Assert.Null(await store.FindServiceClientByClientIdAsync("svc_missing", default));
    }
}
