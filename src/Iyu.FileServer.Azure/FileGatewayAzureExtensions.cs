using Iyu.Core.Attachments;
using Iyu.FileServer;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.FileServer.Azure;

/// <summary>Registers the file gateway with an Azure Blob Storage backend.</summary>
public static class FileGatewayAzureExtensions
{
    /// <summary>
    /// Registers the token-gated file gateway backed by <b>Azure Blob</b> storage. Authenticate with a connection string, or with
    /// <see cref="AzureBlobOptions.ServiceUri"/> and a <see cref="AzureBlobOptions.Credential"/> the host supplies (a managed
    /// identity, for a storage account that has shared-key access turned off).
    /// </summary>
    public static IServiceCollection AddIyuFileGateway(
        this IServiceCollection services,
        Action<FileGatewayOptions> configureGateway,
        Action<AzureBlobOptions> configureBlob)
    {
        ArgumentNullException.ThrowIfNull(configureBlob);
        var blob = new AzureBlobOptions();
        configureBlob(blob);
        blob.Validate();

        services.AddIyuFileGateway(configureGateway);
        services.AddSingleton(blob);
        services.AddSingleton<IAttachmentStorage, AzureBlobAttachmentStorage>();
        return services;
    }
}
