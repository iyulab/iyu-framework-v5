using System.Text;
using Iyu.Core.Attachments;
using Microsoft.Extensions.DependencyInjection;

namespace Iyu.FileServer;

/// <summary>DI + endpoint helpers for a token-gated Azure Blob file gateway host.</summary>
public static partial class FileGatewayExtensions
{
    public static IServiceCollection AddIyuFileGateway(
        this IServiceCollection services,
        Action<FileGatewayOptions> configureGateway,
        Action<AzureBlobOptions> configureBlob)
    {
        ArgumentNullException.ThrowIfNull(configureGateway);
        ArgumentNullException.ThrowIfNull(configureBlob);

        var gw = new FileGatewayOptions();
        configureGateway(gw);
        if (string.IsNullOrEmpty(gw.SigningKey) || Encoding.UTF8.GetByteCount(gw.SigningKey) < 32)
            throw new ArgumentException("FileGatewayOptions.SigningKey must be at least 32 bytes (256 bits) for HMAC-SHA256.", nameof(configureGateway));

        var blob = new AzureBlobOptions();
        configureBlob(blob);

        services.AddSingleton(gw);
        services.AddSingleton(blob);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<FileAccessTokenService>();
        services.AddSingleton<IAttachmentStorage, AzureBlobAttachmentStorage>();
        return services;
    }
}
