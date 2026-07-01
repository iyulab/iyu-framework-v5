using Iyu.MainServer.Identity;
using Xunit;

namespace Iyu.Tests.Identity;

public class ServiceClientServiceTests
{
    [Fact]
    public async Task Create_RejectsPermsExceedingOwner()
    {
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: ["orders.read"]);
        var svc = new ServiceClientService(store, store);   // FakeIdentityStore also implements IServiceClientStore

        var r = await svc.CreateAsync(owner, "tool", ["orders.read", "settlement.write"], null, default);

        Assert.False(r.Ok);
        Assert.Equal(new[] { "settlement.write" }, r.Exceeding);
        Assert.Null(r.PlaintextSecret);
    }

    [Fact]
    public async Task Create_ReturnsPlaintextOnce_AndPersistsHashOnly()
    {
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: ["orders.read", "orders.write"]);
        var svc = new ServiceClientService(store, store);

        var r = await svc.CreateAsync(owner, "tool", ["orders.read"], null, default);

        Assert.True(r.Ok);
        Assert.StartsWith("svc_", r.ClientId);
        Assert.False(string.IsNullOrWhiteSpace(r.PlaintextSecret));
        // 저장은 해시만 — 조회한 클라이언트의 SecretHash != 평문, 그러나 Verify는 통과
        var persisted = await store.FindServiceClientByClientIdAsync(r.ClientId!, default);
        Assert.NotNull(persisted);
        Assert.NotEqual(r.PlaintextSecret, persisted!.SecretHash);
        Assert.True(ServiceClientSecrets.Verify(r.PlaintextSecret!, persisted.SecretHash));
    }

    [Fact]
    public async Task Revoke_OnlyByOwner()
    {
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: ["orders.read"]);
        var stranger = store.AddUser("x", "남", perms: []);
        var svc = new ServiceClientService(store, store);
        var created = await svc.CreateAsync(owner, "tool", ["orders.read"], null, default);
        var id = created.Id;

        Assert.False(await svc.RevokeAsync(id, stranger, default));   // 남은 못 폐기
        Assert.True(await svc.RevokeAsync(id, owner, default));       // 소유자는 폐기
    }
}
