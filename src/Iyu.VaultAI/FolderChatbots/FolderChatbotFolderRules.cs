namespace Iyu.VaultAI.FolderChatbots;

/// <summary>
/// The rules about what a folder tree means, with no file system in them (a folder source gives the engine paths and text; these decide
/// what to do with them).
/// </summary>
/// <remarks>
/// A bot's settings are read from standard file names, none required, names matched ignoring case:
/// <list type="bullet">
/// <item><c>{root}/AGENTS.md</c> — instructions for all bots (the shared agent's instructions).</item>
/// <item><c>{root}/{bot}/AGENTS.md</c> — that bot's instructions (its scope's instructions, appended after the agent's).</item>
/// <item><c>{root}/{bot}/mcp.json</c> — that bot's MCP connections (the connectors its scope uses).</item>
/// </list>
/// The two settings files directly under a bot's folder are not documents and are not indexed (a file of the same name deeper down is one).
/// </remarks>
internal static class FolderChatbotFolderRules
{
    /// <summary>The name of the instructions file (the root one and a bot's).</summary>
    public const string AgentsFileName = "AGENTS.md";

    /// <summary>The name of a bot's MCP settings file.</summary>
    public const string McpFileName = "mcp.json";

    /// <summary>
    /// The built-in instructions of the shared agent. They are agent <b>behavior</b> that existing installations already run with, in
    /// the language they were written in, so they are kept as they are: rewording them changes what an existing agent does the next
    /// time its configuration is applied.
    /// </summary>
    public const string DefaultInstructions =
        """
        당신은 이 챗봇에 연결된 저장소의 문서만 근거로 답한다.
        - 인사 · 잡담이 아니면 답하기 전에 반드시 저장소를 검색한다. 검색 없이 기억이나 짐작으로 답하지 않는다.
        - 사용자의 표현 그대로 한 번, 문서에서 쓸 법한 용어(알람·에러 코드, 부품명, 화면 · 절차 이름)로 바꿔 한 번 이상 더 검색한다. 서너 번 바꿔도 없으면 그만 찾는다.
        - 근거가 된 문장마다 검색 결과의 번호를 붙인다. 파일 이름 · 쪽을 말할 때는 검색 결과에 실제로 나온 것만 쓴다.
        - 문서에서 찾지 못하면 지어내지 말고 «문서에서 찾지 못했다»고 말한 뒤, 검색 결과 중 가장 가까운 위치를 알려 준다. 문서 밖의 추측(다른 시스템 · 담당 부서 등)을 덧붙이지 않는다.
        - 확신이 없는 조치를 단정하지 않는다.
        """;

    /// <summary>
    /// The agent's instructions: the built-in ones (always first, never replaced) and the root <c>AGENTS.md</c>, joined by a blank
    /// line. A file that is empty or only whitespace is the same as none.
    /// </summary>
    public static string ComposeAgentInstructions(string? rootAgents)
        => string.IsNullOrWhiteSpace(rootAgents)
            ? DefaultInstructions.Trim()
            : DefaultInstructions.Trim() + "\n\n" + rootAgents.Trim();

    /// <summary>
    /// A scope's instructions: the bot's <c>AGENTS.md</c> with surrounding whitespace trimmed; null when empty or whitespace only — in a
    /// scoped session vault-ai appends them after the agent's instructions with one blank line between.
    /// </summary>
    public static string? ComposeScopeInstructions(string? botAgents)
        => string.IsNullOrWhiteSpace(botAgents) ? null : botAgents.Trim();

    /// <summary>Whether a path relative to a bot's folder is a bot settings file: <c>AGENTS.md</c> or <c>mcp.json</c> directly under it. Case-insensitive.</summary>
    public static bool IsBotSettingsFile(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        return !path.Contains('/')
               && (string.Equals(path, AgentsFileName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(path, McpFileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a path relative to the root is a settings file: the root's <c>AGENTS.md</c>, or an <c>AGENTS.md</c> / <c>mcp.json</c>
    /// directly under a bot's folder. A watcher uses it so that a change to settings is not missed just because they are not indexed.
    /// </summary>
    public static bool IsSettingsPath(string relativeToRoot)
    {
        var path = relativeToRoot.Replace('\\', '/').TrimStart('/');
        var slash = path.IndexOf('/');
        if (slash < 0) return string.Equals(path, AgentsFileName, StringComparison.OrdinalIgnoreCase);
        return !IsExcluded(path[..slash]) && IsBotSettingsFile(path[(slash + 1)..]);
    }

    /// <summary>
    /// Whether a relative path (a bot's folder name from the root, or a file path from a bot's folder) is excluded by built-in rule: any
    /// segment that starts with a dot (<c>.git</c>, <c>.obsidian</c>, …), a name that starts with <c>~$</c> (an Office lock file), or
    /// <c>Thumbs.db</c> / <c>desktop.ini</c>. Bot settings files are left out separately, by <see cref="IsBotSettingsFile"/>.
    /// </summary>
    public static bool IsExcluded(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        if (path.Split('/').Any(seg => seg.StartsWith('.'))) return true;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.StartsWith("~$", StringComparison.Ordinal)
               || string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase);
    }
}
