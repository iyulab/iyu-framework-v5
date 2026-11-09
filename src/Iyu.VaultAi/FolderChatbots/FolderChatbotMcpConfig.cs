using System.Text.Json;
using System.Text.RegularExpressions;

namespace Iyu.VaultAi.FolderChatbots;

/// <summary>A problem found in a bot's settings: which field, and what is wrong. A warning does not stop the settings from being used.</summary>
internal sealed record ValidationIssue(string Field, string Message, bool IsWarning = false);

/// <summary>
/// One entry of <c>mcp.json</c>. With <see cref="Url"/> the engine creates and owns an MCP connector (named
/// <c>{prefix}{bot}/{name}</c>); with <see cref="ConnectorRef"/> it only looks up a connector that already exists in vault-ai by its
/// exact name and attaches it. Exactly one of the two is present.
/// </summary>
internal sealed record McpEntry(string Name, string? Url, IReadOnlyDictionary<string, string> Headers, string? ConnectorRef);

/// <summary>What parsing found. An entry with an error is left out of <see cref="Entries"/> and is reported in <see cref="Issues"/>.</summary>
internal sealed record McpParseResult(IReadOnlyList<McpEntry> Entries, IReadOnlyList<ValidationIssue> Issues)
{
    public static McpParseResult Empty { get; } = new([], []);

    /// <summary>Whether there is at least one error (a warning does not count).</summary>
    public bool HasErrors => Issues.Any(i => !i.IsWarning);
}

/// <summary>
/// A bot's <c>mcp.json</c>, the file directly under its folder. Its form is
/// <c>{"mcpServers": {"&lt;name&gt;": {"url", "headers"?} | {"connector"}}}</c>. <c>command</c> / <c>args</c> (running a local process) are
/// not something vault-ai supports and are an error. A <c>{{KEY}}</c> in a header value is replaced by vault-ai with a workspace secret —
/// the engine passes it on as it is.
/// </summary>
/// <remarks>
/// <b>Who decides where a secret goes.</b> The folder is written by the people who keep the documents, and a <c>url</c> entry with a
/// <c>{{KEY}}</c> header tells vault-ai to send that workspace secret to that address. So without further restriction, whoever can write a
/// bot's folder can name any workspace secret and any address — a trust the workspace administrator may not have meant to hand them. The
/// restriction is an allow-list of hosts (<c>allowedHosts</c> of <see cref="Parse"/>): when one is given, a <c>url</c> entry whose host is not on
/// it is an error and is left out, so a secret reference can only travel to a host the administrator named. With no list (the default,
/// what existing installations run with) every host is accepted, as before; the engine says so in its log.
/// </remarks>
internal static partial class FolderChatbotMcpConfig
{
    /// <summary>A literal header value this long or longer is suspected to be a secret written into the file.</summary>
    private const int LiteralHeaderWarnLength = 20;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NamePattern();

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Empty or whitespace-only counts as no file (an empty result — every connector the engine owns for the bot is cleaned up).
    /// <paramref name="allowedHosts"/>, when not null, is the set of hosts a <c>url</c> entry may point to (host names, compared ignoring case; a
    /// port is not part of it); an empty set allows no <c>url</c> entry at all. Null puts no restriction on the address. Entries that
    /// only reference an existing connector are not affected — those are the administrator's own objects.
    /// </summary>
    public static McpParseResult Parse(string? json, IReadOnlySet<string>? allowedHosts = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return McpParseResult.Empty;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException ex)
        {
            return new McpParseResult([], [new ValidationIssue(FolderChatbotFolderRules.McpFileName, "The JSON is malformed: " + ex.Message)]);
        }

        using (doc)
        {
            var issues = new List<ValidationIssue>();
            var entries = new List<McpEntry>();
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return new McpParseResult([], [new ValidationIssue(FolderChatbotFolderRules.McpFileName, "The top level must be an object.")]);
            if (!root.TryGetProperty("mcpServers", out var servers))
                return McpParseResult.Empty;
            if (servers.ValueKind != JsonValueKind.Object)
                return new McpParseResult([], [new ValidationIssue("mcpServers", "Must be an object.")]);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in servers.EnumerateObject())
            {
                var field = "mcpServers." + prop.Name;
                if (!NamePattern().IsMatch(prop.Name))
                {
                    issues.Add(new ValidationIssue(field, "A name must be 1 to 64 letters, digits, underscores or hyphens."));
                    continue;
                }
                if (!seen.Add(prop.Name))
                {
                    issues.Add(new ValidationIssue(field, "The same name appears twice."));
                    continue;
                }
                if (ParseEntry(prop.Name, prop.Value, field, issues, allowedHosts) is { } entry) entries.Add(entry);
            }
            return new McpParseResult(entries, issues);
        }
    }

    private static McpEntry? ParseEntry(string name, JsonElement e, string field, List<ValidationIssue> issues, IReadOnlySet<string>? allowedHosts)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ValidationIssue(field, "An entry must be an object."));
            return null;
        }
        if (e.TryGetProperty("command", out _) || e.TryGetProperty("args", out _))
        {
            issues.Add(new ValidationIssue(field, "command / args (running a local process) are not supported — use url or connector."));
            return null;
        }

        var hasUrl = e.TryGetProperty("url", out var urlEl);
        var hasConnector = e.TryGetProperty("connector", out var connEl);
        if (hasUrl == hasConnector)
        {
            issues.Add(new ValidationIssue(field, hasUrl ? "url and connector cannot be used together." : "url or connector is required."));
            return null;
        }

        if (hasConnector)
        {
            if (connEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(connEl.GetString()))
            {
                issues.Add(new ValidationIssue(field + ".connector", "Must be a non-empty string."));
                return null;
            }
            if (e.TryGetProperty("headers", out _))
            {
                issues.Add(new ValidationIssue(field + ".headers", "headers cannot be used on a connector entry."));
                return null;
            }
            return new McpEntry(name, null, new Dictionary<string, string>(), connEl.GetString()!);
        }

        var url = urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            issues.Add(new ValidationIssue(field + ".url", "Must be an absolute address starting with http:// or https://."));
            return null;
        }
        if (allowedHosts is not null && !allowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
        {
            // The host is not echoed with the list: the message goes to logs and screens, and the list is the administrator's.
            issues.Add(new ValidationIssue(field + ".url",
                $"The host '{uri.IdnHost}' is not one the administrator allows for MCP addresses, so this entry is not used."));
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var ok = true;
        if (e.TryGetProperty("headers", out var headersEl))
        {
            if (headersEl.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ValidationIssue(field + ".headers", "Must be an object."));
                return null;
            }
            foreach (var h in headersEl.EnumerateObject())
            {
                var hf = field + ".headers." + h.Name;
                if (h.Value.ValueKind != JsonValueKind.String)
                {
                    issues.Add(new ValidationIssue(hf, "Must be a string."));
                    ok = false;
                    continue;
                }
                var value = h.Value.GetString()!;
                headers[h.Name] = value;
                if (value.Length >= LiteralHeaderWarnLength && !value.Contains("{{", StringComparison.Ordinal))
                    issues.Add(new ValidationIssue(hf,
                        "This looks like a secret written into the file as it is — point to a workspace secret with {{KEY}} instead.", IsWarning: true));
            }
        }
        return ok ? new McpEntry(name, url, headers, null) : null;
    }
}
