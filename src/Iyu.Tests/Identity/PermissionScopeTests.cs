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
            ownerPermissions: ["orders.read", "payments.read"]);
        Assert.Equal(new[] { "orders.read" }, eff);
    }

    [Fact]
    public void Exceeding_ReturnsRequestedNotOwnedByOwner()
    {
        var ex = PermissionScope.Exceeding(
            requested: ["orders.read", "settlement.write"],
            ownerPermissions: ["orders.read"]);
        Assert.Equal(new[] { "settlement.write" }, ex);
    }
}
