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
        var store = new FakeIdentityStore();
        services.AddSingleton<IServiceClientStore>(store);       // 소비앱이 하는 concrete 등록 흉내
        services.AddSingleton(store.Claims);
        services.AddIyuIdentity(
            new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef", Issuer = "iyu", Audience = "iyu-api" },
            permissionCatalog: ["orders.read", "orders.write"]);
        services.AddIyuServiceClients();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddIyuServiceClients_registers_the_token_service_and_the_management_service()
    {
        using var sp = Build();
        Assert.NotNull(sp.GetService<ServiceClientTokenService>());
        Assert.NotNull(sp.GetService<ServiceClientService>());
    }

    /// <summary>
    /// The core alone registers no service-client service — a host that does not issue machine credentials needs no
    /// store for them, and Development's container validation does not ask for one.
    /// </summary>
    [Fact]
    public void AddIyuIdentity_alone_registers_no_service_client_or_credential_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef" },
            permissionCatalog: ["orders.read"]);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ServiceClientTokenService)
                                             || d.ServiceType == typeof(ServiceClientService)
                                             || d.ServiceType == typeof(UserCredentialService));
        Assert.Contains(services, d => d.ServiceType == typeof(UserAccessTokenService));
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

        Assert.Throws<ArgumentException>(() =>
            services.AddIyuIdentity(
                new IdentityTokenOptions { SigningKey = "short", Issuer = "iyu", Audience = "iyu-api" },
                permissionCatalog: ["orders.read", "orders.write"]));
    }

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// 호스트가 자기 시계를 먼저 등록해 뒀으면 그것이 남는다. 이 모듈은 토큰 만료·시크릿 회전
    /// 시각을 <see cref="TimeProvider"/>에서 읽으므로 «어떤» 시계가 이기는지가 실제 동작을 가른다 —
    /// 마지막 등록이 이기는 DI 규칙 때문에 평범한 <c>AddSingleton</c>이면 호스트 것이 조용히 밀린다.
    /// </summary>
    [Fact]
    public void A_host_registered_TimeProvider_survives_AddIyuIdentity()
    {
        var clock = new FakeClock();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddIyuIdentity(
            new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef", Issuer = "iyu", Audience = "iyu-api" },
            permissionCatalog: ["orders.read"]);

        using var sp = services.BuildServiceProvider();
        Assert.Same(clock, sp.GetRequiredService<TimeProvider>());
    }
}
