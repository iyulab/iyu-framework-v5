using System.Text.Json;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>The local record of one file uploaded to vault-ai.</summary>
internal sealed record FileState(Guid RemoteId, long Size, DateTimeOffset LastWrite, string Sha256);

/// <summary>The record of one MCP connector the engine owns. <see cref="Hash"/> is the hash of the name, description, address and headers last applied.</summary>
internal sealed record ConnectorState(Guid Id, string Hash);

/// <summary>One bot's scope as last applied to the agent: its instructions and connectors.</summary>
internal sealed record ScopeState(string? Instructions, IReadOnlyList<Guid> ConnectorIds);

/// <summary>One bot's record: the files uploaded, the connectors the engine owns, the scope last applied, and whether its deletion is pending.</summary>
internal sealed class BotState
{
    /// <summary>When the folder was first seen missing; null when it was last seen present.</summary>
    public DateTimeOffset? PendingDeleteSince { get; set; }

    /// <summary>
    /// The scope last applied to the agent. While the bot's settings files are broken (frozen), or its folder is gone and deletion is
    /// pending, this value is sent again unchanged. Null: nothing applied yet.
    /// </summary>
    public ScopeState? Scope { get; set; }

    /// <summary>The vault-ai MCP connectors the engine created or took over, by the name of the entry in the bot's MCP settings. Entries that only reference a connector are not the engine's and are not recorded.</summary>
    public Dictionary<string, ConnectorState> Connectors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by path relative to the bot's folder. The file's name on the vault-ai side is <c>{bot}/{relative path}</c> (<see cref="FolderChatbotNames.RemoteFileName"/>).</summary>
    public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Files the server refused as an unsupported kind (415): relative path to the SHA-256 at that time. A file with the same content is not sent again.</summary>
    public Dictionary<string, string> Unsupported { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The engine's local state: the pairing of folders with vault-ai objects and files. It holds data only; where it is kept is the
/// host's concern (the engine reads and writes it as JSON through <see cref="Parse"/> and <see cref="ToJson"/>).
/// </summary>
internal sealed class FolderChatbotManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// This sync instance's owner id. Decided once at first creation and stamped in the description of the storage it created or took
    /// over — so that objects of another instance (same workspace and prefix) are not adopted by name alone.
    /// </summary>
    public Guid Owner { get; set; }

    /// <summary>The prefix this manifest was written for. If the configuration differs, the manifest is discarded and the engine starts afresh (null: not recorded yet).</summary>
    public string? Prefix { get; set; }

    /// <summary>The vault-ai address this manifest was written for. If the configuration differs, the manifest is discarded (null: not recorded yet).</summary>
    public string? Url { get; set; }

    /// <summary>
    /// The models this instance last picked. They are "the models in use" at the next check — kept while the workspace's candidates
    /// come and go (<see cref="FolderChatbotModelPicker"/>). Null: nothing picked yet (the existing storage is read to decide).
    /// </summary>
    public FolderChatbotModels? Models { get; set; }

    /// <summary>The storage, agent and web channel all bots share (named <c>{prefix}all</c>). Null: not created yet.</summary>
    public Guid? StorageId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? ChannelId { get; set; }

    /// <summary>The hash of the agent configuration (model, instructions, scopes, connectors) last applied. If it differs, the agent is updated (null: not matched yet).</summary>
    public string? AgentConfigHash { get; set; }

    /// <summary>The agent instructions last applied. A check that cannot read the root instructions file uses this unchanged.</summary>
    public string? AgentInstructions { get; set; }

    /// <summary>The hash of the storage configuration (models, description line) last applied.</summary>
    public string? StorageConfigHash { get; set; }

    /// <summary>
    /// The storage's language (index) model as of the last time the files that could not be indexed were reprocessed. If it differs from
    /// the model now (null included), those files are processed again — a file uploaded without a model is not indexed by itself once a model exists.
    /// </summary>
    public Guid? StorageLanguageModel { get; set; }

    /// <summary>Per bot (scope). Keyed by folder name, case-sensitive.</summary>
    public Dictionary<string, BotState> Bots { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The manifest in <paramref name="json"/>; an empty one when it is null or blank.</summary>
    public static FolderChatbotManifest Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FolderChatbotManifest();
        var loaded = JsonSerializer.Deserialize<FolderChatbotManifest>(json, JsonOptions) ?? new FolderChatbotManifest();
        // The dictionaries are made ordinal again after deserializing; a null entry in a hand-edited file counts as absent.
        loaded.Bots = new Dictionary<string, BotState>(
            (loaded.Bots ?? []).Where(kv => kv.Value is not null), StringComparer.Ordinal);
        foreach (var bot in loaded.Bots.Values)
        {
            bot.Files = new Dictionary<string, FileState>(
                (bot.Files ?? []).Where(kv => kv.Value is not null), StringComparer.Ordinal);
            bot.Connectors = new Dictionary<string, ConnectorState>(
                (bot.Connectors ?? []).Where(kv => kv.Value is not null), StringComparer.Ordinal);
            bot.Unsupported = new Dictionary<string, string>(
                (bot.Unsupported ?? []).Where(kv => kv.Value is not null), StringComparer.Ordinal);
            if (bot.Scope is { ConnectorIds: null } scope) bot.Scope = scope with { ConnectorIds = [] };
        }
        return loaded;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>A deep copy, for a read-only view of the engine's state: it does not change while a check edits the original.</summary>
    public FolderChatbotManifest Clone() => Parse(ToJson());
}
