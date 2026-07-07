using System.Collections.Generic;
using BareChat;
using Iyu.Server.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.Chat;

public class IyuChatGateTests
{
    private static IConfiguration BuildConfig(bool enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:Enabled"] = enabled ? "true" : "false"
            })
            .Build();

    [Fact]
    public void AddIyuChat_WhenDisabled_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddIyuChat(BuildConfig(enabled: false));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IChatAuthProvider));
    }

    [Fact]
    public void AddIyuChat_WhenSectionMissing_RegistersNothing()
    {
        var services = new ServiceCollection();
        var empty = new ConfigurationBuilder().Build();

        services.AddIyuChat(empty);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IChatAuthProvider));
    }

    [Fact]
    public void AddIyuChat_WhenEnabled_RegistersBareChat()
    {
        var services = new ServiceCollection();

        services.AddIyuChat(BuildConfig(enabled: true));

        // AddBareChat가 등록하는 마커 서비스 — 활성화되면 존재해야 한다.
        Assert.Contains(services, d => d.ServiceType == typeof(IChatAuthProvider));
    }
}
