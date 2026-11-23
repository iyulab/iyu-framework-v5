using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>What registering the folder chatbot engine shares with its tests: where its record lives and how a configured path is read.</summary>
internal static class FolderChatbotServiceCollectionExtensions
{
    /// <summary>The place the engine keeps its record unless told otherwise: the file existing installations already have.</summary>
    internal static readonly string[] DefaultManifestSegments = ["App_Data", "vault-ai-chatbots.manifest.json"];

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
internal sealed class FolderChatbotOptionsValidator(IOptions<VaultAISettings> vaultAI) : IValidateOptions<VaultAIFolderChatbotOptions>
{
    public ValidateOptionsResult Validate(string? name, VaultAIFolderChatbotOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;

        var settings = vaultAI.Value;
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            return Fail("VaultAI:Url must be the absolute http(s) address of vault-ai when the folder chatbots are enabled.");
        if (string.IsNullOrWhiteSpace(settings.Token))
            return Fail("VaultAI:Token must be set when the folder chatbots are enabled (a key that can change vault-ai's settings).");
        if (settings.RequestTimeout <= TimeSpan.Zero)
            return Fail("VaultAI:RequestTimeout has to be greater than zero.");

        if (string.IsNullOrWhiteSpace(options.Path))
            return Fail($"{nameof(VaultAIFolderChatbotOptions.Path)} must name the folder that holds the chatbot folders.");
        if (string.IsNullOrWhiteSpace(options.Prefix))
            return Fail($"{nameof(VaultAIFolderChatbotOptions.Prefix)} must not be blank (it keeps the engine away from objects it did not make).");
        if (options.RescanMinutes < 1)
            return Fail($"{nameof(VaultAIFolderChatbotOptions.RescanMinutes)} must be at least 1.");

        if (options.McpAllowedHosts is { } hosts && hosts.Any(h => !IsHostName(h)))
        {
            return Fail($"{nameof(VaultAIFolderChatbotOptions.McpAllowedHosts)} entries must be plain host names " +
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
