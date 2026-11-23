using Iyu.VaultAI.FolderChatbots;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.VaultAI;

/// <summary>Registers the folder chatbot engine of a host.</summary>
public static class VaultAIFolderChatbotExtensions
{
    /// <summary>
    /// Adds the folder chatbot engine - a background service that keeps the chatbot folders and vault-ai in line. Nothing is registered
    /// unless <c>VaultAI:Chatbots:Enabled</c> is true (no service, no route, no client). A host that turns it on without what it needs does
    /// not start: the message names the setting that is missing or wrong, never its value.
    /// </summary>
    /// <remarks>
    /// Reads <c>VaultAI:Url</c> and <c>VaultAI:Token</c> (the key must be able to change settings) and <c>VaultAI:Chatbots</c>. It shares
    /// <see cref="VaultAISettings"/> with the report module without needing it: the two can be added together or alone.
    /// </remarks>
    public static IServiceCollection AddVaultAIFolderChatbots(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var vaultAI = configuration.GetSection("VaultAI");
        var section = vaultAI.GetSection("Chatbots");
        if (!section.GetValue<bool>(nameof(VaultAIFolderChatbotOptions.Enabled))) return services;

        services.Configure<VaultAISettings>(vaultAI);
        services.AddOptions<VaultAIFolderChatbotOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<VaultAIFolderChatbotOptions>, FolderChatbotOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFolderChatbotAdmin>(sp =>
            FolderChatbotAdminClient.Create(sp.GetRequiredService<IOptions<VaultAISettings>>().Value));
        services.TryAddSingleton<IFolderChatbotSource>(sp =>
            new FileSystemFolderChatbotSource(FolderChatbotServiceCollectionExtensions.ResolveUnderContentRoot(
                sp.GetRequiredService<IOptions<VaultAIFolderChatbotOptions>>().Value.Path,
                sp.GetRequiredService<IHostEnvironment>().ContentRootPath)));
        services.TryAddSingleton<IFolderChatbotManifestStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VaultAIFolderChatbotOptions>>().Value;
            var root = sp.GetRequiredService<IHostEnvironment>().ContentRootPath;
            return new FileFolderChatbotManifestStore(string.IsNullOrWhiteSpace(options.ManifestPath)
                ? Path.Combine([root, .. FolderChatbotServiceCollectionExtensions.DefaultManifestSegments])
                : FolderChatbotServiceCollectionExtensions.ResolveUnderContentRoot(options.ManifestPath, root));
        });
        services.TryAddSingleton(sp => new FolderChatbotSyncService(
            sp.GetRequiredService<IOptions<VaultAIFolderChatbotOptions>>().Value,
            sp.GetRequiredService<IFolderChatbotAdmin>(),
            sp.GetRequiredService<IFolderChatbotSource>(),
            sp.GetRequiredService<IFolderChatbotManifestStore>(),
            sp.GetRequiredService<IOptions<VaultAISettings>>().Value.Url,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<FolderChatbotSyncService>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<FolderChatbotStatusReader>();
        services.AddSingleton<FolderChatbotSyncHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<FolderChatbotSyncHostedService>());
        return services;
    }
}
