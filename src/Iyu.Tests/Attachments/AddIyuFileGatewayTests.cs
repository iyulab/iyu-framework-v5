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
}
