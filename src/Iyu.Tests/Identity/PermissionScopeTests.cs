using Iyu.MainServer.Identity;
using Xunit;

namespace Iyu.Tests.Identity;

public class PermissionScopeTests
{
    [Fact]
    public void Effective_IsIntersection_SortedDistinct()
    {
        var eff = PermissionScope.Effective(
            requested: ["orders.write", "orders.read", "orders.read"],
            ownerPermissions: ["orders.read", "payments.read"],
            PermissionHierarchy.Flat);
        Assert.Equal(new[] { "orders.read" }, eff);
    }

    [Fact]
    public void Exceeding_ReturnsRequestedNotOwnedByOwner()
    {
        var ex = PermissionScope.Exceeding(
            requested: ["orders.read", "settlement.write"],
            ownerPermissions: ["orders.read"],
            PermissionHierarchy.Flat);
        Assert.Equal(new[] { "settlement.write" }, ex);
    }

    /// <summary>
    /// 소유자는 자기 코드가 함의하는 것도 갖는다 — <c>orders.write</c> 만 가진 소유자가 <c>orders.read</c> 클라이언트를
    /// 낼 수 있다. 반대 방향(좁은 코드의 소유자가 넓은 코드)은 여전히 넘는다.
    /// </summary>
    [Fact]
    public void An_owner_holds_what_their_codes_imply()
    {
        var hierarchy = new PermissionHierarchy(
            ["orders.read", "orders.write"], [new("orders.write", "orders.read")]);

        Assert.Equal(new[] { "orders.read" },
            PermissionScope.Effective(["orders.read"], ownerPermissions: ["orders.write"], hierarchy));
        Assert.Empty(PermissionScope.Exceeding(["orders.read"], ownerPermissions: ["orders.write"], hierarchy));
        Assert.Equal(new[] { "orders.write" },
            PermissionScope.Exceeding(["orders.write"], ownerPermissions: ["orders.read"], hierarchy));
    }
}
