using System.Text;
using Iyu.Core.Attachments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iyu.FileServer;

/// <summary>DI + endpoint helpers for a token-gated Azure Blob file gateway host.</summary>
public static partial class FileGatewayExtensions
{
    /// <summary>Registers the token-gated file gateway backed by <b>Azure Blob</b> storage.</summary>
    public static IServiceCollection AddIyuFileGateway(
        this IServiceCollection services,
        Action<FileGatewayOptions> configureGateway,
        Action<AzureBlobOptions> configureBlob)
    {
        ArgumentNullException.ThrowIfNull(configureBlob);
        var blob = new AzureBlobOptions();
        configureBlob(blob);

        AddGatewayCore(services, configureGateway);
        services.AddSingleton(blob);
        services.AddSingleton<IAttachmentStorage, AzureBlobAttachmentStorage>();
        return services;
    }

    /// <summary>Registers the token-gated file gateway backed by the <b>local filesystem</b> (on-prem/NAS hosting).</summary>
    public static IServiceCollection AddIyuFileGateway(
        this IServiceCollection services,
        Action<FileGatewayOptions> configureGateway,
        Action<FileSystemOptions> configureFileSystem)
    {
        ArgumentNullException.ThrowIfNull(configureFileSystem);
        var fs = new FileSystemOptions();
        configureFileSystem(fs);

        AddGatewayCore(services, configureGateway);
        services.AddSingleton(fs);
        services.AddSingleton<IAttachmentStorage, FileSystemAttachmentStorage>();
        return services;
    }

    /// <summary>Backend-agnostic gateway registration shared by every storage overload.</summary>
    private static void AddGatewayCore(IServiceCollection services, Action<FileGatewayOptions> configureGateway)
    {
        ArgumentNullException.ThrowIfNull(configureGateway);

        var gw = new FileGatewayOptions();
        configureGateway(gw);
        if (string.IsNullOrEmpty(gw.SigningKey) || Encoding.UTF8.GetByteCount(gw.SigningKey) < 32)
            throw new ArgumentException("FileGatewayOptions.SigningKey must be at least 32 bytes (256 bits) for HMAC-SHA256.", nameof(configureGateway));

        services.AddSingleton(gw);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<FileAccessTokenService>();
    }

    public static IEndpointRouteBuilder MapIyuFileGateway(this IEndpointRouteBuilder app)
    {
        var gw = app.ServiceProvider.GetRequiredService<FileGatewayOptions>();
        // Resolved once and captured, rather than injected per request: the category is fixed, so a
        // per-request lookup would buy nothing. Optional throughout — a host with no logging configured
        // still gets a working gateway.
        var logger = app.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);

        app.MapPut(gw.RoutePrefix, (HttpRequest req, string? token, FileAccessTokenService tk, IAttachmentStorage st, FileGatewayOptions o, CancellationToken ct)
            => FileGatewayHandlers.UploadAsync(req, token, tk, st, o, ct, logger));
        app.MapGet(gw.RoutePrefix, (string token, FileAccessTokenService tk, IAttachmentStorage st, FileGatewayOptions o, CancellationToken ct)
            => FileGatewayHandlers.DownloadAsync(token, tk, st, o, ct, logger));
        app.MapMethods(gw.RoutePrefix, new[] { HttpMethods.Head }, (string token, FileAccessTokenService tk, IAttachmentStorage st, FileGatewayOptions o, CancellationToken ct)
            => FileGatewayHandlers.ExistsAsync(token, tk, st, o, ct, logger));
        app.MapDelete(gw.RoutePrefix, (string? token, HttpRequest req, FileAccessTokenService tk, IAttachmentStorage st, FileGatewayOptions o, CancellationToken ct)
            => FileGatewayHandlers.DeleteAsync(token, req, tk, st, o, ct, logger));
        return app;
    }

    /// <summary>Log category for every gateway decision, so a host can filter or raise the level for the byte
    /// gateway alone without touching the rest of its logging.</summary>
    public const string LogCategory = "Iyu.FileServer.FileGateway";
}
