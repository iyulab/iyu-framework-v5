using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iyu.VaultAi.FolderChatbots;

/// <summary>One bot's check state: how many files the last check skipped, how many actions failed, and when its folder was first seen missing.</summary>
internal sealed record FolderChatbotBotMeta(
    [property: JsonPropertyName("p")] DateTimeOffset? PendingDeleteSince,
    [property: JsonPropertyName("s")] int Skipped,
    [property: JsonPropertyName("f")] int Failed)
{
    [JsonIgnore]
    public bool IsDefault => PendingDeleteSince is null && Skipped == 0 && Failed == 0;
}

/// <summary>
/// The state the sync leaves in the shared storage's description. The line is <c>chatbot-meta:</c> followed by one line of JSON,
/// <c>{"owner":…,"bots":{"&lt;bot&gt;":{"p":…,"s":n,"f":n}}}</c>, and a screen reads it to show each bot's state.
/// <see cref="Owner"/> is the id of the sync instance that created or took over the storage; <see cref="Bots"/> holds only the bots
/// that are not at the default (not pending deletion, nothing skipped, nothing failed). vault-ai caps a storage description at
/// <see cref="MaxLength"/> characters, so when the line would be longer the bots pending deletion are kept first and the rest dropped.
/// </summary>
/// <remarks>
/// Like <see cref="FolderChatbotNames"/> this is a stored data format: the line already exists in installations that run folder
/// chatbots, and other readers (a desktop catalogue, an older engine being replaced) read it by the same prefix and keys. Changing
/// it changes what they see; the contract tests pin the exact text.
/// </remarks>
internal sealed record FolderChatbotStorageMeta(Guid? Owner, IReadOnlyDictionary<string, FolderChatbotBotMeta> Bots)
{
    /// <summary>The prefix of the description line.</summary>
    public const string Prefix = "chatbot-meta:";

    /// <summary>The longest storage description vault-ai accepts.</summary>
    public const int MaxLength = 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The description string. Bots at the default are left out; past <see cref="MaxLength"/> the ones pending deletion come first,
    /// then the failed, then the skipped, as many as fit. <paramref name="dropped"/> is how many bots the length left out.
    /// </summary>
    public string Format(out int dropped)
    {
        var candidates = Bots.Where(kv => !kv.Value.IsDefault)
            .OrderByDescending(kv => kv.Value.PendingDeleteSince is not null)
            .ThenByDescending(kv => kv.Value.Failed)
            .ThenByDescending(kv => kv.Value.Skipped)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        var kept = new SortedDictionary<string, FolderChatbotBotMeta>(StringComparer.Ordinal);
        dropped = 0;
        foreach (var (bot, meta) in candidates)
        {
            kept[bot] = meta;
            if (Serialize(kept).Length <= MaxLength) continue;
            kept.Remove(bot);
            dropped++;
        }
        return Serialize(kept);
    }

    public string Format() => Format(out _);

    private string Serialize(IReadOnlyDictionary<string, FolderChatbotBotMeta> bots)
        => Prefix + JsonSerializer.Serialize(new Dto { Owner = Owner, Bots = bots.Count == 0 ? null : bots }, Json);

    /// <summary>False when the description lacks the prefix or its JSON is broken.</summary>
    public static bool TryParse(string? description, out FolderChatbotStorageMeta? meta)
    {
        meta = null;
        if (description is null || !description.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(description.AsSpan(Prefix.Length), Json);
            if (dto is null) return false;
            meta = new FolderChatbotStorageMeta(dto.Owner,
                new Dictionary<string, FolderChatbotBotMeta>(
                    dto.Bots ?? new Dictionary<string, FolderChatbotBotMeta>(), StringComparer.Ordinal));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class Dto
    {
        public Guid? Owner { get; set; }
        public IReadOnlyDictionary<string, FolderChatbotBotMeta>? Bots { get; set; }
    }
}
