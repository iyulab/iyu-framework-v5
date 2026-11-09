namespace Iyu.VaultAi.FolderChatbots;

/// <summary>
/// Settings of the folder chatbot engine. Each first-level folder under <see cref="Path"/> is one chatbot. Off unless
/// <see cref="Enabled"/> is set.
/// </summary>
/// <remarks>
/// <b>Who can write a bot's folder is trusted with more than documents.</b> A bot's <c>mcp.json</c> names the MCP connectors its scope
/// uses, and a <c>url</c> entry with a <c>{{KEY}}</c> header tells vault-ai to send that workspace secret to that address. Set
/// <see cref="McpAllowedHosts"/> to the hosts those addresses may point at, so that a secret reference can only travel where the administrator
/// said. Left unset, any host is accepted (what existing installations run with) and the engine says so in its log. A <c>connector</c> entry can
/// only attach a connector set up for general use - never one a sync engine owns for another bot.
/// </remarks>
internal sealed class VaultAiFolderChatbotOptions
{
    /// <summary>The default prefix of the vault-ai resource names. It matches the one existing installations already use.</summary>
    public const string DefaultPrefix = "chatbot:";

    public bool Enabled { get; set; }

    /// <summary>The root of the chatbot folders. A bot's settings live inside it too: the root <c>AGENTS.md</c> and each bot's <c>AGENTS.md</c> and <c>mcp.json</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The prefix of the vault-ai resource names. One sync instance per workspace and prefix: use another prefix for development and tests,
    /// or two engines will delete each other's files.
    /// </summary>
    public string Prefix { get; set; } = DefaultPrefix;

    /// <summary>How often the folders are scanned again, in minutes.</summary>
    public int RescanMinutes { get; set; } = 10;

    /// <summary>How long a bot is kept after its folder vanished before it is really deleted, in hours (less than 1 counts as 1).</summary>
    public int DeleteGraceHours { get; set; } = 24;

    /// <summary>A file larger than this (MB) is not uploaded and is skipped. Zero or less: no limit.</summary>
    public int MaxFileMB { get; set; } = 200;

    /// <summary>
    /// Take over a storage of the same name that another sync instance made, in a check where the manifest records no shared storage yet
    /// (no manifest, a new start after the prefix or address changed, or only refused so far). Normally false: adopting another instance's
    /// objects makes this one delete its files. Turn it back off after the take-over.
    /// </summary>
    public bool AdoptForeign { get; set; }

    /// <summary>
    /// The hosts an <c>mcp.json</c> <c>url</c> may point at (host names, compared ignoring case; a port is not part of it). Null: any host.
    /// An empty list: no <c>url</c> entry at all.
    /// </summary>
    public List<string>? McpAllowedHosts { get; set; }

    /// <summary>The models to use; blank picks from the workspace's list (see <see cref="FolderChatbotModelPicker"/>).</summary>
    public VaultAiFolderChatbotModelOptions Models { get; set; } = new();

    /// <summary>
    /// Where the engine keeps its record of what it uploaded (JSON). Blank: <c>App_Data/vault-ai-chatbots.manifest.json</c> under the
    /// content root - the file existing installations already have.
    /// </summary>
    public string ManifestPath { get; set; } = "";
}
