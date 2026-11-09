namespace Iyu.VaultAi.FolderChatbots;

/// <summary>A settings file as read: its text (null when absent or only whitespace), or why it could not be read.</summary>
internal sealed record SettingsText(string? Text, string? Error);

/// <summary>
/// Where the bots' settings files come from. The engine does not assume a file system: a host (or the bundled folder source) answers
/// these, and the rules about what the text means stay in <see cref="FolderChatbotConfig"/>. Names are matched ignoring case; if several
/// files differ only by case, an implementation picks one and says so in <c>warning</c>.
/// </summary>
internal interface IFolderChatbotSettingsReader
{
    /// <summary>The <c>AGENTS.md</c> of the root (<paramref name="bot"/> null) or of a bot's folder.</summary>
    SettingsText ReadAgents(string? bot, out string? warning);

    /// <summary>The <c>mcp.json</c> of a bot's folder.</summary>
    SettingsText ReadMcp(string bot, out string? warning);
}

/// <summary>
/// A check's view of the settings in the folders: the shared agent's instructions, each bot's scope instructions and MCP entries, and
/// which of them cannot be trusted this time. A value is read once and remembered for the whole check, so that a file changing while the
/// check runs cannot make one bot's settings flicker.
/// </summary>
internal sealed class FolderChatbotConfig
{
    private readonly IFolderChatbotSettingsReader _reader;
    private readonly IReadOnlySet<string>? _allowedHosts;
    private readonly SettingsText _rootAgents;
    private readonly Dictionary<string, SettingsText> _botAgents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpParseResult> _botMcp = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _botWarnings = new(StringComparer.Ordinal);

    /// <param name="models">The models picked at the start of the check (<see cref="FolderChatbotModelPicker"/>).</param>
    /// <param name="allowedHosts">The hosts an <c>mcp.json</c> address may point at; null accepts any (see <see cref="FolderChatbotMcpConfig"/>).</param>
    public FolderChatbotConfig(
        IFolderChatbotSettingsReader reader, FolderChatbotModels models, IReadOnlySet<string>? allowedHosts = null)
    {
        _reader = reader;
        _allowedHosts = allowedHosts;
        Models = models;
        _rootAgents = reader.ReadAgents(null, out var warning);
        var issues = new List<string>();
        if (warning is not null) issues.Add(warning);
        if (_rootAgents.Error is { } error) issues.Add($"{FolderChatbotFolderRules.AgentsFileName}: {error}");
        RootIssues = issues;
    }

    public FolderChatbotModels Models { get; }

    /// <summary>Problems with the root <c>AGENTS.md</c>. If it could not be read, the agent's instructions stay as last applied.</summary>
    public IReadOnlyList<string> RootIssues { get; }

    /// <summary>Whether the root <c>AGENTS.md</c> could not be read — the agent's instructions are then left as last applied.</summary>
    public bool IsRootFrozen => _rootAgents.Error is not null;

    /// <summary>The shared agent's instructions (<see cref="FolderChatbotFolderRules.ComposeAgentInstructions"/>).</summary>
    public string AgentInstructions => FolderChatbotFolderRules.ComposeAgentInstructions(_rootAgents.Text);

    /// <summary>A bot's scope instructions; null when its <c>AGENTS.md</c> is absent or empty.</summary>
    public string? ScopeInstructions(string bot) => FolderChatbotFolderRules.ComposeScopeInstructions(BotAgents(bot).Text);

    /// <summary>
    /// The result of parsing the bot's <c>mcp.json</c>. No file is an empty result. A read failure or a malformed file is not thrown: it
    /// lands in <see cref="McpParseResult.Issues"/> and only that bot's MCP state freezes — the check goes on.
    /// </summary>
    public McpParseResult McpOf(string bot)
    {
        lock (_botMcp)
        {
            if (_botMcp.TryGetValue(bot, out var result)) return result;
            var file = _reader.ReadMcp(bot, out var warning);
            if (warning is not null) WarningsOf(bot).Add(warning);
            return _botMcp[bot] = file.Error is { } error
                ? new McpParseResult([], [new ValidationIssue(FolderChatbotFolderRules.McpFileName, "cannot be read: " + error)])
                : file.Text is null ? McpParseResult.Empty : FolderChatbotMcpConfig.Parse(file.Text, _allowedHosts);
        }
    }

    /// <summary>Problems with a bot's settings files (an unreadable <c>AGENTS.md</c>, several names differing only by case). Entry errors of <c>mcp.json</c> are in <see cref="McpOf"/>.</summary>
    public IReadOnlyList<string> IssuesOf(string bot)
    {
        var agents = BotAgents(bot);
        McpOf(bot);
        lock (_botWarnings)
        {
            return [.. WarningsOf(bot), .. agents.Error is { } e ? [$"{FolderChatbotFolderRules.AgentsFileName}: {e}"] : Array.Empty<string>()];
        }
    }

    /// <summary>
    /// Whether this bot's scope stays as last applied until its settings are fixed: its instructions file could not be read, or
    /// <c>mcp.json</c> has an error. Applying part of a bot's settings would leave a state nobody asked for, so the connectors are left
    /// alone and the scope is the one last applied.
    /// </summary>
    public bool IsScopeFrozen(string bot) => BotAgents(bot).Error is not null || McpOf(bot).HasErrors;

    /// <summary>The bot's <c>mcp.json</c> entries. A file with an error yields none — the engine freezes the bot's MCP state instead (<see cref="IsScopeFrozen"/>).</summary>
    public IReadOnlyList<McpEntry> McpEntriesOf(string bot)
    {
        var result = McpOf(bot);
        return result.HasErrors ? [] : result.Entries;
    }

    private SettingsText BotAgents(string bot)
    {
        lock (_botAgents)
        {
            if (_botAgents.TryGetValue(bot, out var text)) return text;
            text = _reader.ReadAgents(bot, out var warning);
            if (warning is not null) WarningsOf(bot).Add(warning);
            return _botAgents[bot] = text;
        }
    }

    private List<string> WarningsOf(string bot)
    {
        lock (_botWarnings)
        {
            if (!_botWarnings.TryGetValue(bot, out var list)) _botWarnings[bot] = list = [];
            return list;
        }
    }
}
