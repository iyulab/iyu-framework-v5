using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.VaultAi.FolderChatbots;

/// <summary>Registers the folder chatbot engine.</summary>
internal static class FolderChatbotServiceCollectionExtensions
{
    /// <summary>The place the engine keeps its record unless told otherwise: the file existing installations already have.</summary>
    internal static readonly string[] DefaultManifestSegments = ["App_Data", "vault-ai-chatbots.manifest.json"];

    /// <summary>
    /// Adds the folder chatbot engine - a background service that keeps the chatbot folders and vault-ai in line. Nothing is registered
    /// unless <c>VaultAi:Chatbots:Enabled</c> is true (no service, no route, no client). A host that turns it on without what it needs does
    /// not start: the message names the setting that is missing or wrong, never its value.
    /// </summary>
    /// <remarks>
    /// Reads <c>VaultAi:Url</c> and <c>VaultAi:Token</c> (the key must be able to change settings) and <c>VaultAi:Chatbots</c>. It shares
    /// <see cref="VaultAiSettings"/> with the report module without needing it: the two can be added together or alone.
    /// </remarks>
    public static IServiceCollection AddVaultAiFolderChatbots(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var vaultAi = configuration.GetSection("VaultAi");
        var section = vaultAi.GetSection("Chatbots");
        if (!section.GetValue<bool>(nameof(VaultAiFolderChatbotOptions.Enabled))) return services;

        services.Configure<VaultAiSettings>(vaultAi);
        services.AddOptions<VaultAiFolderChatbotOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<VaultAiFolderChatbotOptions>, FolderChatbotOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFolderChatbotAdmin>(sp =>
            FolderChatbotAdminClient.Create(sp.GetRequiredService<IOptions<VaultAiSettings>>().Value));
        services.TryAddSingleton<IFolderChatbotSource>(sp =>
            new FileSystemFolderChatbotSource(ResolveUnderContentRoot(
                sp.GetRequiredService<IOptions<VaultAiFolderChatbotOptions>>().Value.Path,
                sp.GetRequiredService<IHostEnvironment>().ContentRootPath)));
        services.TryAddSingleton<IFolderChatbotManifestStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VaultAiFolderChatbotOptions>>().Value;
            var root = sp.GetRequiredService<IHostEnvironment>().ContentRootPath;
            return new FileFolderChatbotManifestStore(string.IsNullOrWhiteSpace(options.ManifestPath)
                ? Path.Combine([root, .. DefaultManifestSegments])
                : ResolveUnderContentRoot(options.ManifestPath, root));
        });
        services.TryAddSingleton(sp => new FolderChatbotSyncService(
            sp.GetRequiredService<IOptions<VaultAiFolderChatbotOptions>>().Value,
            sp.GetRequiredService<IFolderChatbotAdmin>(),
            sp.GetRequiredService<IFolderChatbotSource>(),
            sp.GetRequiredService<IFolderChatbotManifestStore>(),
            sp.GetRequiredService<IOptions<VaultAiSettings>>().Value.Url,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<FolderChatbotSyncService>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<FolderChatbotSyncHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<FolderChatbotSyncHostedService>());
        return services;
    }

    /// <summary>A relative path is taken from the content root; a blank one stays blank (it never turns into the content root itself).</summary>
    internal static string ResolveUnderContentRoot(string path, string contentRoot)
        => string.IsNullOrWhiteSpace(path)
            ? ""
            : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path));
}

/// <summary>
/// Refuses to start a host that turned the folder chatbots on without what they need. Each message names the setting, never a value:
/// startup errors end up in logs and on screens.
/// </summary>
internal sealed class FolderChatbotOptionsValidator(IOptions<VaultAiSettings> vaultAi) : IValidateOptions<VaultAiFolderChatbotOptions>
{
    public ValidateOptionsResult Validate(string? name, VaultAiFolderChatbotOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;

        var settings = vaultAi.Value;
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            return Fail("VaultAi:Url must be the absolute http(s) address of vault-ai when the folder chatbots are enabled.");
        if (string.IsNullOrWhiteSpace(settings.Token))
            return Fail("VaultAi:Token must be set when the folder chatbots are enabled (a key that can change vault-ai's settings).");
        if (settings.RequestTimeout <= TimeSpan.Zero)
            return Fail("VaultAi:RequestTimeout has to be greater than zero.");

        if (string.IsNullOrWhiteSpace(options.Path))
            return Fail($"{nameof(VaultAiFolderChatbotOptions.Path)} must name the folder that holds the chatbot folders.");
        if (string.IsNullOrWhiteSpace(options.Prefix))
            return Fail($"{nameof(VaultAiFolderChatbotOptions.Prefix)} must not be blank (it keeps the engine away from objects it did not make).");
        if (options.RescanMinutes < 1)
            return Fail($"{nameof(VaultAiFolderChatbotOptions.RescanMinutes)} must be at least 1.");

        if (options.McpAllowedHosts is { } hosts && hosts.Any(h => !IsHostName(h)))
        {
            return Fail($"{nameof(VaultAiFolderChatbotOptions.McpAllowedHosts)} entries must be plain host names " +
                        "(no scheme, port, path, credentials or spaces).");
        }
        return ValidateOptionsResult.Success;
    }

    private static ValidateOptionsResult Fail(string message) => ValidateOptionsResult.Fail(message);

    /// <summary>A host name: letters, digits, dots, hyphens (and underscores) - the list is compared with the host an address names, so anything else could never match and is a mistake.</summary>
    private static bool IsHostName(string? host)
        => !string.IsNullOrWhiteSpace(host) && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
                                              && host[0] != '.' && host[^1] != '.';
}
