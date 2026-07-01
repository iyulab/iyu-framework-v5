using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Identity;

public class AddIyuIdentityTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIdentityStore>(new FakeIdentityStore());       // 소비앱이 하는 concrete 등록 흉내
        services.AddSingleton<IServiceClientStore>(sp => (FakeIdentityStore)sp.GetRequiredService<IIdentityStore>());
        services.AddIyuIdentity(
            new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef", Issuer = "iyu", Audience = "iyu-api" },
            permissionCatalog: ["orders.read", "orders.write"]);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registers_TokenService_AndServiceClientService()
    {
        using var sp = Build();
        Assert.NotNull(sp.GetService<IdentityTokenService>());
        Assert.NotNull(sp.GetService<ServiceClientService>());
    }

    [Fact]
    public async Task Registers_PermissionPolicies_ForCatalog()
    {
        using var sp = Build();
        var authz = sp.GetRequiredService<IAuthorizationPolicyProvider>();
        Assert.NotNull(await authz.GetPolicyAsync("orders.read"));
        Assert.NotNull(await authz.GetPolicyAsync("orders.write"));
    }

    [Fact]
    public void Registers_JwtBearer_Scheme()
    {
        using var sp = Build();
        // 등록만 검증: JwtBearer 핸들러 옵션이 존재
        Assert.NotNull(sp.GetService<IConfigureOptions<JwtBearerOptions>>());
    }

    [Fact]
    public void ShortSigningKey_ThrowsArgumentException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIdentityStore>(new FakeIdentityStore());
        services.AddSingleton<IServiceClientStore>(sp => (FakeIdentityStore)sp.GetRequiredService<IIdentityStore>());

        Assert.Throws<ArgumentException>(() =>
            services.AddIyuIdentity(
                new IdentityTokenOptions { SigningKey = "short", Issuer = "iyu", Audience = "iyu-api" },
                permissionCatalog: ["orders.read", "orders.write"]));
    }
}
