using Iyu.Core.Entities;
using Iyu.Core.Identity;
using Xunit;

namespace Iyu.Tests.Identity;

public class ContractTests
{
    private sealed class TestUser : IyuUser { }
    private sealed class TestServiceClient : IyuServiceClient { }

    // An account row that stores no hash for an account with no password (NULL, not a fake value) implements the
    // contract as it is — no explicit-interface adapter. With TreatWarningsAsErrors, a non-nullable IUser.PasswordHash
    // makes this file fail to build (CS8766), which is the regression this class stands guard against.
    private sealed class AccountRow : IUser
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = "";
        public string? PasswordHash { get; init; }
        public string DisplayName { get; init; } = "";
        public string? Email { get; init; }
        public bool IsActive { get; init; } = true;
        public DateTimeOffset? LastLoginAt { get; init; }
    }

    [Fact]
    public void An_account_with_no_password_carries_no_hash()
    {
        IUser service = new AccountRow { Id = Guid.NewGuid(), Username = "svc-billing" };
        Assert.Null(service.PasswordHash);
        Assert.Null(((IUser)new TestUser()).PasswordHash);
    }

    [Fact]
    public void IyuUser_ImplementsIUser_AndIyuEntity()
    {
        var u = new TestUser
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
        var c = new TestServiceClient
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
