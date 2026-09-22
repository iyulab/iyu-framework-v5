using Iyu.Core.Attachments;
using Iyu.FileServer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Attachments;

public sealed class AddIyuFileGatewayTests
{
    private const string Key = "0123456789abcdef0123456789abcdef"; // 32 bytes

    [Fact]
    public void Registers_token_service_and_storage()
    {
        var services = new ServiceCollection();
        services.AddIyuFileGateway(
            gw => { gw.SigningKey = Key; },
            blob => { blob.ConnectionString = "UseDevelopmentStorage=true"; blob.ContainerName = "test"; });
        var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<FileAccessTokenService>());
        Assert.NotNull(sp.GetService<IAttachmentStorage>());
        Assert.NotNull(sp.GetService<FileGatewayOptions>());
    }

    [Fact]
    public void Fails_fast_on_short_key()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() =>
            services.AddIyuFileGateway(gw => { gw.SigningKey = "tooshort"; }, blob => { blob.ConnectionString = "x"; }));
    }

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// A host that registered its own clock keeps it. The gateway needs <em>a</em> TimeProvider for
    /// access-token expiry, not specifically the system one, so its registration is a default —
    /// plain <c>AddSingleton</c> would win on last-registration-wins and silently replace the host's.
    /// </summary>
    [Fact]
    public void A_host_registered_TimeProvider_survives_the_gateway_registration()
    {
        var clock = new FakeClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);

        services.AddIyuFileGateway(
            gw => { gw.SigningKey = Key; },
            blob => { blob.ConnectionString = "UseDevelopmentStorage=true"; blob.ContainerName = "test"; });

        var sp = services.BuildServiceProvider();
        Assert.Same(clock, sp.GetRequiredService<TimeProvider>());
    }
}
