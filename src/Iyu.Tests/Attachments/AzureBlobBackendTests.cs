using Azure.Core;
using Iyu.Core.Attachments;
using Iyu.FileServer;
using Iyu.FileServer.Azure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Attachments;

/// <summary>
/// The Azure Blob backend is its own package: the gateway package carries no Azure SDK, and the backend authenticates with a
/// connection string or — for a storage account with shared-key access off — a token credential the host supplies. Nothing
/// here touches the network: constructing the container client performs no I/O.
/// </summary>
public sealed class AzureBlobBackendTests
{
    private const string Key = "0123456789abcdef0123456789abcdef"; // 32 bytes

    private sealed class FixedCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    [Fact]
    public void The_gateway_package_carries_no_Azure_SDK()
    {
        var referenced = typeof(FileGatewayOptions).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(referenced, n => n.StartsWith("Azure.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Azure_package_asks_for_no_identity_library_the_host_has_not_chosen()
    {
        var referenced = typeof(AzureBlobOptions).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain("Azure.Identity", referenced);
    }

    [Fact]
    public void A_connection_string_registers_the_backend()
    {
        var sp = Build(blob => { blob.ConnectionString = "UseDevelopmentStorage=true"; blob.ContainerName = "test"; });

        Assert.IsType<AzureBlobAttachmentStorage>(sp.GetRequiredService<IAttachmentStorage>());
    }

    [Fact]
    public void A_service_uri_with_a_host_supplied_credential_registers_the_backend_without_a_key()
    {
        var sp = Build(blob =>
        {
            blob.ServiceUri = new Uri("https://account.blob.core.windows.net");
            blob.Credential = new FixedCredential();
        });

        Assert.IsType<AzureBlobAttachmentStorage>(sp.GetRequiredService<IAttachmentStorage>());
    }

    [Fact]
    public void Setting_neither_way_to_authenticate_is_refused_where_it_is_registered()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(blob => { blob.ContainerName = "test"; }));

        Assert.Contains("ConnectionString", ex.Message);
    }

    [Fact]
    public void Setting_both_ways_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(blob =>
        {
            blob.ConnectionString = "UseDevelopmentStorage=true";
            blob.ServiceUri = new Uri("https://account.blob.core.windows.net");
            blob.Credential = new FixedCredential();
        }));

        Assert.Contains("both", ex.Message);
    }

    [Fact]
    public void A_service_uri_without_a_credential_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Build(blob => { blob.ServiceUri = new Uri("https://account.blob.core.windows.net"); }));
    }

    [Fact]
    public void A_credential_without_a_service_uri_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Build(blob => { blob.Credential = new FixedCredential(); }));
    }

    [Fact]
    public void A_relative_service_uri_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Build(blob =>
        {
            blob.ServiceUri = new Uri("/account", UriKind.Relative);
            blob.Credential = new FixedCredential();
        }));
    }

    [Fact]
    public void An_empty_container_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Build(blob =>
        {
            blob.ConnectionString = "UseDevelopmentStorage=true";
            blob.ContainerName = " ";
        }));
    }

    [Theory]
    [InlineData("https://account.blob.core.windows.net", "attachments", "https://account.blob.core.windows.net/attachments")]
    [InlineData("https://account.blob.core.windows.net/", "attachments", "https://account.blob.core.windows.net/attachments")]
    // The storage emulator addresses the account in the path.
    [InlineData("http://127.0.0.1:10000/devstoreaccount1/", "files", "http://127.0.0.1:10000/devstoreaccount1/files")]
    [InlineData("https://account.blob.core.windows.net", "a b", "https://account.blob.core.windows.net/a%20b")]
    public void The_container_is_addressed_under_the_service_uri(string serviceUri, string container, string expected)
    {
        var options = new AzureBlobOptions { ServiceUri = new Uri(serviceUri), Credential = new FixedCredential(), ContainerName = container };

        Assert.Equal(expected, options.ContainerUri().AbsoluteUri);
    }

    [Fact]
    public void The_gateway_alone_registers_no_storage()
    {
        var services = new ServiceCollection();
        services.AddIyuFileGateway(gw => { gw.SigningKey = Key; });
        var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<FileAccessTokenService>());
        Assert.Null(sp.GetService<IAttachmentStorage>());
    }

    private static ServiceProvider Build(Action<AzureBlobOptions> configureBlob)
    {
        var services = new ServiceCollection();
        services.AddIyuFileGateway(gw => { gw.SigningKey = Key; }, configureBlob);
        return services.BuildServiceProvider();
    }
}
