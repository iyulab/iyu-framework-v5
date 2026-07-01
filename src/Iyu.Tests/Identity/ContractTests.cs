using Iyu.Core.Entities;
using Iyu.Core.Identity;
using Xunit;

namespace Iyu.Tests.Identity;

public class ContractTests
{
    [Fact]
    public void IyuUser_ImplementsIUser_AndIyuEntity()
    {
        var u = new IyuUser
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = "hash",
            DisplayName = "관리자",
            IsActive = true,
        };
        Assert.IsAssignableFrom<IUser>(u);
        Assert.IsAssignableFrom<IyuEntity>(u);
        Assert.Equal("admin", ((IUser)u).Username);
    }

    [Fact]
    public void IyuServiceClient_ImplementsIServiceClient_WithOwner()
    {
        var owner = Guid.NewGuid();
        var c = new IyuServiceClient
        {
            Id = Guid.NewGuid(),
            ClientId = "svc_abc",
            SecretHash = "h",
            DisplayName = "tool",
            OwnerUserId = owner,
            IsActive = true,
        };
        Assert.IsAssignableFrom<IServiceClient>(c);
        Assert.Equal(owner, ((IServiceClient)c).OwnerUserId);
    }
}
