using System.Net;
using Microsoft.Extensions.Logging;

namespace Iyu.VaultAi.FolderChatbots;

/// <summary>What one bot's check counted.</summary>
internal sealed class BotTally
{
    public int Applied;
    public int Failed;
    public int Skipped;
}

/// <summary>
/// The shared storage found by name belongs to another sync instance (a different owner id), so it is not taken over. The check does
/// nothing.
/// </summary>
internal sealed class FolderChatbotForeignOwnerException(string name, Guid owner)
    : InvalidOperationException(
        $"The vault-ai storage '{name}' belongs to another sync instance (owner {owner}), so it is not taken over and folder chatbot " +
        "sync is skipped. Keep one sync instance per workspace and prefix (use another prefix for development and tests). If it really " +
        "should be taken over, allow adopting another instance's objects and run one check from a manifest that records no shared storage.")
{
    public string StorageName { get; } = name;
    public Guid Owner { get; } = owner;
}

/// <summary>
/// The shared storage found by name uses an embedding model other than the one this check would use. Changing it without being told to
/// re-embeds the whole storage, so it is not taken over and the check does nothing.
/// </summary>
internal sealed class FolderChatbotEmbeddingMismatchException(string name, Guid storageModel, Guid? common)
    : InvalidOperationException(
        $"The vault-ai storage '{name}' uses embedding model {storageModel}, so it is not switched to the common model ({common}) and " +
        "folder chatbot sync is skipped (switching would embed the whole storage again). To switch deliberately, set the embedding model explicitly.")
{
    public string StorageName { get; } = name;
}

/// <summary>What the parts of the executor share during one check.</summary>
internal sealed class FolderChatbotSyncContext(
    IFolderChatbotAdmin client, string prefix, FolderChatbotConfig config, FolderChatbotManifest manifest,
    Action saveManifest, ILogger log)
{
    public IFolderChatbotAdmin Client { get; } = client;
    public string Prefix { get; } = prefix;
    public FolderChatbotConfig Config { get; } = config;
    public FolderChatbotManifest Manifest { get; } = manifest;
    public ILogger Log { get; } = log;

    /// <summary>
    /// Whether this check may take over another instance's storage: only when adopting is allowed <b>and</b> the manifest recorded no
    /// shared storage when the check began.
    /// </summary>
    public bool AdoptForeign { get; init; }

    /// <summary>
    /// Whether the embedding model of this check was set explicitly. If not, a storage taken over by name keeps its own embedding model
    /// even when it differs from the common one — changing it makes vault-ai embed the whole storage again.
    /// </summary>
    public bool EmbeddingExplicit { get; init; }

    /// <summary>Messages to add to the check's warnings.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>The name of the shared objects, <c>{prefix}all</c>.</summary>
    public string SharedName => FolderChatbotNames.SharedName(Prefix);

    /// <summary>
    /// The owner id on a storage found by name when it differs from this instance's. A storage with no owner id (made before the id was
    /// stamped, or by hand) is null: it is taken over and stamped.
    /// </summary>
    public Guid? ForeignOwnerOf(NamedRef storage)
        => FolderChatbotStorageMeta.TryParse(storage.Description, out var meta) && meta!.Owner is { } owner && owner != Manifest.Owner
            ? owner
            : null;

    /// <summary>Called after every action that changes state, so that stopping part-way does not forget what was done remotely.</summary>
    public void SaveManifest() => saveManifest();

    public static bool IsNotFound(Exception ex) => ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound };

    /// <summary>The entry of a prefix listing whose name is exactly <paramref name="name"/>.</summary>
    public static NamedRef? ByName(IEnumerable<NamedRef> list, string name)
        => list.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    /// <summary>The storage is gone remotely — the file records for it are void too (the files go to the new storage).</summary>
    public static void ForgetStorage(FolderChatbotManifest m)
    {
        m.StorageId = null;
        m.StorageConfigHash = null;
        m.StorageLanguageModel = null;
        foreach (var bot in m.Bots.Values) bot.Files.Clear();
    }

    /// <summary>The agent is gone remotely — the channel pointed at the old agent, so it is made again with it.</summary>
    public static void ForgetAgent(FolderChatbotManifest m)
    {
        m.AgentId = null;
        m.AgentConfigHash = null;
        m.ChannelId = null;
    }
}
