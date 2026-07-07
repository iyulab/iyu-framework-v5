using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using BareChat;
using Iyu.Server.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.Chat;

/// <summary>
/// 표시명 매핑 검증: iyu는 ClaimTypes.Name 에 로그인 ID, GivenName 에 사람 이름을 싣고,
/// 어댑터가 표시명을 사람 이름(GivenName) 우선으로 투영해야 한다. (INT-2)
/// </summary>
public class IyuChatAuthProviderTests
{
    private static HttpContext AuthedContext(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    [Fact]
    public async Task ResolveUser_MapsGivenNameToDisplayName()
    {
        var provider = new IyuChatAuthProvider();
        var ctx = AuthedContext(
            new Claim(ClaimTypes.NameIdentifier, "260"),
            new Claim(ClaimTypes.Name, "iyulab-admin"),     // 로그인 ID
            new Claim(ClaimTypes.GivenName, "정안종"));       // 사람 이름

        var user = await provider.ResolveUserAsync(ctx);

        Assert.True(user.IsAuthenticated);
        Assert.Equal("260", user.UserId);
        Assert.Equal("정안종", user.DisplayName);
    }

    [Fact]
    public async Task ResolveUser_FallsBackToNameWhenNoGivenName()
    {
        var provider = new IyuChatAuthProvider();
        var ctx = AuthedContext(
            new Claim(ClaimTypes.NameIdentifier, "260"),
            new Claim(ClaimTypes.Name, "iyulab-admin"));

        var user = await provider.ResolveUserAsync(ctx);

        Assert.Equal("iyulab-admin", user.DisplayName);
    }

    [Fact]
    public async Task ResolveUser_AnonymousWhenUnauthenticated()
    {
        var provider = new IyuChatAuthProvider();
        var ctx = new DefaultHttpContext(); // 인증되지 않은 기본 principal

        var user = await provider.ResolveUserAsync(ctx);

        Assert.False(user.IsAuthenticated);
    }

    [Fact]
    public void AddIyuChat_WhenEnabled_ReplacesAuthProviderWithIyuMapping()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:Enabled"] = "true" })
            .Build();

        services.AddIyuChat(config);

        var provider = services.BuildServiceProvider().GetRequiredService<IChatAuthProvider>();
        Assert.IsType<IyuChatAuthProvider>(provider);
    }
}
