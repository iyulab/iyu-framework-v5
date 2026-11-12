using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>
/// The vault-ai objects all bots share (a storage, an agent and a web channel, each named <c>{prefix}all</c>): making them, repairing
/// them when they vanish, keeping their settings in line — and each bot's scope on the agent and the MCP connectors the engine owns.
/// Objects are found by name; each prefix listing is read once per check.
/// </summary>
internal sealed class FolderChatbotSharedObjects(FolderChatbotSyncContext cx)
{
    private List<NamedRef>? _storages;
    private List<NamedRef>? _agents;
    private List<NamedRef>? _channels;
    private List<NamedRef>? _prefixedConnectors;
    private List<NamedRef>? _allConnectors;
    private readonly HashSet<string> _warnedConnectors = new(StringComparer.Ordinal);

    private FolderChatbotManifest M => cx.Manifest;
    private FolderChatbotModels Models => cx.Config.Models;

    private async Task<List<NamedRef>> StoragesAsync(CancellationToken ct)
        => _storages ??= [.. await cx.Client.ListStoragesAsync(cx.Prefix, ct).ConfigureAwait(false)];

    private async Task<List<NamedRef>> AgentsAsync(CancellationToken ct)
        => _agents ??= [.. await cx.Client.ListAgentsAsync(cx.Prefix, ct).ConfigureAwait(false)];

    private async Task<List<NamedRef>> ChannelsAsync(CancellationToken ct)
        => _channels ??= [.. await cx.Client.ListChannelsAsync(cx.Prefix, ct).ConfigureAwait(false)];

    /// <summary>Connectors whose name starts with the prefix (the ones the engine may own). Read once per check, and only when needed.</summary>
    private async Task<List<NamedRef>> PrefixedConnectorsAsync(CancellationToken ct)
        => _prefixedConnectors ??= [.. await cx.Client.ListConnectorsAsync(cx.Prefix, ct).ConfigureAwait(false)];

    /// <summary>Every connector in the workspace — read only to find one a <c>connector</c> entry names, or to see whether a frozen scope's connector still exists.</summary>
    private async Task<List<NamedRef>> AllConnectorsAsync(CancellationToken ct)
        => _allConnectors ??= [.. await cx.Client.ListConnectorsAsync(null, ct).ConfigureAwait(false)];

    // ---------------- creating and repairing ----------------

    /// <summary>
    /// Makes sure the shared objects exist. If a recorded object has vanished remotely (someone deleted it) it is matched again; if
    /// there is none it is found by name and taken over, or made. A storage that is new or taken over empties every bot's file record —
    /// the next plan uploads again, and where the taken-over storage already holds the same file the upload adopts it.
    /// Two cases do not recreate anything and return a warning instead, and the check then does nothing:
    /// <list type="bullet">
    /// <item>An object missing from a list answers a direct lookup by id (the list is wrong) — making another of the same name would duplicate it.</item>
    /// <item>All three have vanished at once (a server reset, or a token for another workspace).</item>
    /// </list>
    /// A storage found by name that belongs to another sync instance throws <see cref="FolderChatbotForeignOwnerException"/>, and one
    /// that uses a different embedding model <see cref="FolderChatbotEmbeddingMismatchException"/>.
    /// </summary>
    public async Task<string?> EnsureAsync(CancellationToken ct)
    {
        var name = cx.SharedName;
        var storages = await StoragesAsync(ct).ConfigureAwait(false);
        var agents = await AgentsAsync(ct).ConfigureAwait(false);
        var channels = await ChannelsAsync(ct).ConfigureAwait(false);

        var storageLost = M.StorageId is { } s && storages.All(r => r.Id != s);
        var agentLost = M.AgentId is { } a && agents.All(r => r.Id != a);
        var channelLost = M.ChannelId is { } c && channels.All(r => r.Id != c);
        if (storageLost && agentLost && channelLost)
        {
            return "The folder chatbot storage, agent and channel have all vanished from vault-ai at once, so they are not made again " +
                   "and this check is skipped (check the server and the workspace of the token; after a server reset, delete the manifest and start over).";
        }
        var unlisted = new List<string>();
        if (storageLost && await cx.Client.StorageExistsAsync(M.StorageId!.Value, ct).ConfigureAwait(false)) unlisted.Add("storage");
        if (agentLost && await cx.Client.AgentExistsAsync(M.AgentId!.Value, ct).ConfigureAwait(false)) unlisted.Add("agent");
        if (channelLost && await cx.Client.ChannelExistsAsync(M.ChannelId!.Value, ct).ConfigureAwait(false)) unlisted.Add("channel");
        if (unlisted.Count > 0)
        {
            return $"vault-ai's list has no folder chatbot {string.Join(" / ", unlisted)}, but a direct lookup finds it, so the list cannot be trusted: " +
                   "it is not made again and this check is skipped (check the server and the listing).";
        }
        if (storageLost || agentLost || channelLost)
        {
            cx.Log.LogWarning("Folder chatbot: matching again what vanished from vault-ai (storage {Storage}, agent {Agent}, channel {Channel})",
                storageLost, agentLost, channelLost);
            if (storageLost) FolderChatbotSyncContext.ForgetStorage(M);
            if (agentLost) FolderChatbotSyncContext.ForgetAgent(M);
            if (channelLost) M.ChannelId = null;
            cx.SaveManifest();
        }

        if (M.StorageId is null)
        {
            // File records of another storage (an old manifest) must not be mistaken for the new storage's.
            FolderChatbotSyncContext.ForgetStorage(M);
            var existing = FolderChatbotSyncContext.ByName(storages, name);
            if (existing is null)
            {
                var meta = new FolderChatbotStorageMeta(M.Owner, new Dictionary<string, FolderChatbotBotMeta>()).Format();
                var id = await cx.Client.CreateStorageAsync(name, meta, Models.Embedding, Models.Index, Models.Rerank, ct)
                    .ConfigureAwait(false);
                storages.Add(new NamedRef(id, name, meta));
                M.StorageId = id;
                M.StorageLanguageModel = Models.Index;   // new storage: no unindexable files to process again
            }
            else
            {
                if (cx.ForeignOwnerOf(existing) is { } owner)
                {
                    if (!cx.AdoptForeign) throw new FolderChatbotForeignOwnerException(name, owner);
                    cx.Log.LogWarning("Folder chatbot: taking over the storage '{Name}' of another sync instance (owner {Owner}); adopting is allowed", name, owner);
                }
                // Matching the settings after a take-over would switch the embedding model to the common one and embed the whole storage again.
                if (existing is StorageRef { EmbeddingModel: { } used } && used != Models.Embedding && !cx.EmbeddingExplicit)
                    throw new FolderChatbotEmbeddingMismatchException(name, used, Models.Embedding);
                M.StorageId = existing.Id;
            }
            M.StorageConfigHash = null;   // settings and state meta are matched at the end of this check
            cx.SaveManifest();
        }

        var agentCreated = false;
        if (M.AgentId is null)
        {
            var existing = FolderChatbotSyncContext.ByName(agents, name);
            if (existing is null)
            {
                // Scopes and connectors are filled in by the agent reconciliation at the end of this check.
                var instructions = M.AgentInstructions ?? FolderChatbotFolderRules.ComposeAgentInstructions(null);
                var id = await cx.Client.CreateAgentAsync(name, "", Models.Answer, instructions, [], [M.StorageId!.Value], [], ct)
                    .ConfigureAwait(false);
                agents.Add(new NamedRef(id, name, ""));
                M.AgentId = id;
                agentCreated = true;
            }
            else M.AgentId = existing.Id;
            M.AgentConfigHash = null;
            cx.SaveManifest();
        }

        if (M.ChannelId is null || agentCreated)
        {
            var existing = FolderChatbotSyncContext.ByName(channels, name);
            if (existing is not null && agentCreated)
            {
                // A channel of the same name points at the old agent — delete it and make it again for the new one.
                await cx.Client.DeleteChannelAsync(existing.Id, ct).ConfigureAwait(false);
                channels.Remove(existing);
                existing = null;
            }
            if (existing is null)
            {
                var id = await cx.Client.CreateWebChannelAsync(name, M.AgentId!.Value, ct).ConfigureAwait(false);
                channels.Add(new NamedRef(id, name, ""));
                M.ChannelId = id;
            }
            else M.ChannelId = existing.Id;
            cx.SaveManifest();
        }
        return null;
    }

    // ---------------- agent and scopes ----------------

    /// <summary>
    /// Matches the agent: instructions = built-in + root <c>AGENTS.md</c>, scopes = one per bot in the manifest (except
    /// <paramref name="removing"/>), connectors = the union of every scope's connectors. It updates only when the hash changed. A bot whose
    /// folder exists and whose settings are sound gets a scope from today's settings (its connectors matched too); a bot whose settings are
    /// broken, or whose folder has gone and is waiting to be deleted, keeps the scope last applied (minus connectors that have vanished
    /// remotely — a scope may not name a connector the agent does not have). If the root instructions could not be read, the instructions
    /// stay as last applied. A 404 records the agent as lost so that the next check makes it again.
    /// </summary>
    public async Task ReconcileAgentAsync(FolderSnapshot snapshot, IReadOnlySet<string> removing,
        Func<string, BotTally> tallyOf, CancellationToken ct)
    {
        if (M.AgentId is not { } agentId || M.StorageId is not { } storageId) return;

        var instructions = cx.Config.IsRootFrozen
            ? M.AgentInstructions ?? cx.Config.AgentInstructions
            : cx.Config.AgentInstructions;

        var desired = new SortedDictionary<string, ScopeState>(StringComparer.Ordinal);
        foreach (var (bot, state) in M.Bots.Where(kv => !removing.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (snapshot.Bots.ContainsKey(bot) && !cx.Config.IsScopeFrozen(bot))
            {
                try
                {
                    var ids = await ReconcileConnectorsAsync(bot, cx.Config.McpEntriesOf(bot), state, ct).ConfigureAwait(false);
                    desired[bot] = new ScopeState(cx.Config.ScopeInstructions(bot), ids);
                    continue;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Only this bot's connectors failed — other bots' scopes are not held up; this one keeps the scope last applied.
                    cx.Log.LogWarning(ex, "Folder chatbot '{Bot}': matching connectors failed — using the scope last applied", bot);
                    tallyOf(bot).Failed++;
                }
            }
            desired[bot] = await FrozenScopeAsync(state, ct).ConfigureAwait(false);
        }

        var scopes = desired.Select(kv => new AgentScope(kv.Key, [FolderChatbotNames.TagOf(kv.Key)], kv.Value.Instructions,
            kv.Value.ConnectorIds)).ToList();
        var connectors = scopes.SelectMany(s => s.ConnectorIds).Distinct().Order().ToList();
        var hash = AgentHash(instructions, storageId, scopes);
        if (hash != M.AgentConfigHash)
        {
            try
            {
                await cx.Client.UpdateAgentAsync(agentId, cx.SharedName, "", Models.Answer, instructions, connectors,
                    [storageId], scopes, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (FolderChatbotSyncContext.IsNotFound(ex))
            {
                FolderChatbotSyncContext.ForgetAgent(M);
                cx.SaveManifest();
                throw;
            }
            M.AgentConfigHash = hash;
        }
        M.AgentInstructions = instructions;
        foreach (var (bot, scope) in desired) M.Bots[bot].Scope = scope;
        cx.SaveManifest();
    }

    /// <summary>The scope last applied (or, with none, only the connectors the engine is recorded as owning), keeping the connectors that still exist remotely.</summary>
    private async Task<ScopeState> FrozenScopeAsync(BotState state, CancellationToken ct)
    {
        var scope = state.Scope ?? new ScopeState(null, state.Connectors.Values.Select(c => c.Id).Order().ToList());
        if (scope.ConnectorIds.Count == 0) return scope;
        var existing = (await AllConnectorsAsync(ct).ConfigureAwait(false)).Select(r => r.Id).ToHashSet();
        return scope with { ConnectorIds = scope.ConnectorIds.Where(existing.Contains).ToList() };
    }

    // ---------------- storage ----------------

    /// <summary>
    /// Updates the storage only when the hash of its settings (models, state meta) changed; a 404 records the storage as lost. If the
    /// language (index) model differs from the one files that could not be indexed were last reprocessed under, those files are
    /// reprocessed (<see cref="FolderChatbotManifest.StorageLanguageModel"/>).
    /// </summary>
    public async Task ReconcileStorageAsync(Func<string, BotTally> tallyOf, CancellationToken ct)
    {
        if (M.StorageId is not { } storageId) return;
        var bots = M.Bots.ToDictionary(kv => kv.Key,
            kv => new FolderChatbotBotMeta(kv.Value.PendingDeleteSince, tallyOf(kv.Key).Skipped, tallyOf(kv.Key).Failed),
            StringComparer.Ordinal);
        var meta = new FolderChatbotStorageMeta(M.Owner, bots).Format(out var dropped);
        if (dropped > 0)
        {
            cx.Log.LogWarning("The folder chatbot state meta would exceed the storage description limit ({Max} characters), so the state of {Count} bot(s) is left out",
                FolderChatbotStorageMeta.MaxLength, dropped);
        }
        var hash = Hash([Models.Index, Models.Embedding, Models.Rerank, meta]);
        if (hash != M.StorageConfigHash)
        {
            try
            {
                await cx.Client.UpdateStorageAsync(storageId, cx.SharedName, meta, Models.Embedding, Models.Index, Models.Rerank, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (FolderChatbotSyncContext.IsNotFound(ex))
            {
                FolderChatbotSyncContext.ForgetStorage(M);
                cx.SaveManifest();
                throw;
            }
            M.StorageConfigHash = hash;
            cx.SaveManifest();
        }

        if (Models.Index is { } language && M.StorageLanguageModel != language)
        {
            await RetryNotIndexableAsync(storageId, ct).ConfigureAwait(false);
            M.StorageLanguageModel = language;
            cx.SaveManifest();
        }
    }

    /// <summary>
    /// Runs the pipeline again for files that could not be indexed (<c>not_indexable</c>) — a file uploaded without a language model (or
    /// under another one) is not indexed by vault-ai by itself once a model exists. A file being processed (409) or already gone (404) is
    /// passed over. Any other failure is thrown (the next check tries again).
    /// </summary>
    private async Task RetryNotIndexableAsync(Guid storageId, CancellationToken ct)
    {
        var files = await cx.Client.ListFilesAsync(storageId, ct).ConfigureAwait(false);
        var retried = 0;
        foreach (var file in files.Where(f => f.Status == "not_indexable"))
        {
            try
            {
                await cx.Client.RetryFilePipelineAsync(storageId, file.Id, ct).ConfigureAwait(false);
                retried++;
            }
            catch (Exception ex) when (ex is VaultAIConflictException || FolderChatbotSyncContext.IsNotFound(ex)) { }
        }
        if (retried > 0)
            cx.Log.LogInformation("Folder chatbot: the index model changed, so {Count} file(s) that could not be indexed are processed again", retried);
    }

    // ---------------- MCP connectors ----------------

    /// <summary>The name of an engine-owned MCP connector on the vault-ai side: <c>{prefix}{bot}/{entry}</c>.</summary>
    private string ConnectorNameOf(string bot, string entry) => cx.Prefix + bot + "/" + entry;

    /// <summary>The ownership mark — the connector's description carries this instance's owner id as JSON (the same owner id as the storage meta).</summary>
    private string OwnerDescription() => JsonSerializer.Serialize(new { owner = M.Owner });

    /// <summary>The owner id in a connector's description; null when it is in another form or absent (before it was stamped, or made by hand).</summary>
    private static Guid? OwnerOf(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        try
        {
            using var doc = JsonDocument.Parse(description);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("owner", out var owner)
                   && owner.ValueKind == JsonValueKind.String
                   && Guid.TryParse(owner.GetString(), out var id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the connector has an owner id that is not this instance's — it belongs to another instance, so it is neither changed nor deleted.</summary>
    private bool IsForeign(NamedRef connector) => OwnerOf(connector.Description) is { } owner && owner != M.Owner;

    private void Warn(string message)
    {
        cx.Log.LogWarning("{Message}", message);
        cx.Warnings.Add(message);
    }

    private static string ConnectorHash(string name, string description, string url, IReadOnlyDictionary<string, string> headers)
        => Hash([name, description, url, headers.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new[] { kv.Key, kv.Value })]);

    /// <summary>
    /// Turns <c>mcp.json</c> entries into the connector ids a scope uses. A <c>url</c> entry makes the engine-owned connector (if missing)
    /// and updates it when the hash differs; a <c>connector</c> entry looks one up by exact name and only attaches it (a warning and
    /// nothing if missing). A connector owned by another instance is left alone, with a warning. A recorded id that has vanished remotely is
    /// made again. Connectors for entries that were dropped are deleted by <see cref="PruneConnectorsAsync"/> after the agent is updated
    /// (so a connector still in use is never deleted first).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ReconcileConnectorsAsync(string bot, IReadOnlyList<McpEntry> entries, BotState state,
        CancellationToken ct)
    {
        var ids = new List<Guid>();
        foreach (var entry in entries)
        {
            var id = entry.Url is not null
                ? await EnsureOwnedConnectorAsync(bot, entry, state, ct).ConfigureAwait(false)
                : await FindReferencedConnectorAsync(bot, entry, ct).ConfigureAwait(false);
            if (id is { } found) ids.Add(found);
        }
        return ids.Distinct().Order().ToList();
    }

    private async Task<Guid?> EnsureOwnedConnectorAsync(string bot, McpEntry entry, BotState state, CancellationToken ct)
    {
        var name = ConnectorNameOf(bot, entry.Name);
        var description = OwnerDescription();
        var hash = ConnectorHash(name, description, entry.Url!, entry.Headers);
        var list = await PrefixedConnectorsAsync(ct).ConfigureAwait(false);

        state.Connectors.TryGetValue(entry.Name, out var recorded);
        var remote = recorded is null ? null : list.FirstOrDefault(r => r.Id == recorded.Id);
        if (remote is null)
        {
            // No record, or it vanished remotely — take over one of the same name, or make a new one.
            recorded = null;
            state.Connectors.Remove(entry.Name);
            remote = FolderChatbotSyncContext.ByName(list, name);
        }

        if (remote is not null && recorded is null && IsForeign(remote) && cx.AdoptForeign)
        {
            // The same rule as the storage: when adopting is allowed and this check began with a manifest that is starting afresh,
            // take it over and stamp our owner id (the update below).
            cx.Log.LogWarning("Folder chatbot '{Bot}': taking over the connector '{Name}' of another sync instance (owner {Owner}); adopting is allowed",
                bot, name, OwnerOf(remote.Description));
        }
        else if (remote is not null && IsForeign(remote))
        {
            state.Connectors.Remove(entry.Name);
            cx.SaveManifest();
            Warn($"Folder chatbot '{bot}': the vault-ai connector '{name}' belongs to another sync instance (owner {OwnerOf(remote.Description)}), " +
                 $"so it is not touched and the mcp.json entry '{entry.Name}' is not attached.");
            return null;
        }

        if (remote is not null)
        {
            if (recorded is null && OwnerOf(remote.Description) is null)
                cx.Log.LogInformation("Folder chatbot '{Bot}': taking over the connector '{Name}', which carries no owner mark, and stamping our owner id", bot, name);
            if (recorded?.Hash == hash && remote.Name == name && remote.Description == description)
                return remote.Id;
            try
            {
                await cx.Client.UpdateMcpConnectorAsync(remote.Id, name, description, entry.Url!, entry.Headers, ct).ConfigureAwait(false);
                list.Remove(remote);
                list.Add(new NamedRef(remote.Id, name, description));
                state.Connectors[entry.Name] = new ConnectorState(remote.Id, hash);
                cx.SaveManifest();
                return remote.Id;
            }
            catch (Exception ex) when (FolderChatbotSyncContext.IsNotFound(ex))
            {
                list.Remove(remote);   // gone in the meantime — made again below
            }
        }

        var id = await cx.Client.CreateMcpConnectorAsync(name, description, entry.Url!, entry.Headers, ct).ConfigureAwait(false);
        list.Add(new NamedRef(id, name, description));
        _allConnectors?.Add(new NamedRef(id, name, description));
        state.Connectors[entry.Name] = new ConnectorState(id, hash);
        cx.SaveManifest();
        return id;
    }

    private async Task<Guid?> FindReferencedConnectorAsync(string bot, McpEntry entry, CancellationToken ct)
    {
        var all = await AllConnectorsAsync(ct).ConfigureAwait(false);
        if (FolderChatbotSyncContext.ByName(all, entry.ConnectorRef!) is { } found)
        {
            // A reference may attach a connector somebody set up for general use. It may not attach one an engine owns - this instance's
            // or another's: those belong to one bot's scope, and the people who write a bot's folder are not meant to be able to hand
            // their bot another bot's connector (and the secrets its headers carry) just by naming it.
            if (OwnerOf(found.Description) is not null || found.Name.StartsWith(cx.Prefix, StringComparison.Ordinal))
            {
                if (_warnedConnectors.Add(entry.ConnectorRef!))
                {
                    Warn($"Folder chatbot '{bot}': the mcp.json entry '{entry.Name}' names the connector '{found.Name}', which a sync engine owns " +
                         "for one bot's scope; a connector entry can only attach a connector set up for general use, so it is not attached.");
                }
                return null;
            }
            return found.Id;
        }
        if (_warnedConnectors.Add(entry.ConnectorRef!))
        {
            cx.Log.LogWarning("The connector '{Connector}' named in mcp.json cannot be found in vault-ai and is ignored (bot '{Bot}', entry '{Entry}')",
                entry.ConnectorRef, bot, entry.Name);
        }
        return null;
    }

    /// <summary>
    /// Called after the agent is matched: deletes engine-owned connectors for entries that left <c>mcp.json</c> (or stopped being
    /// <c>url</c> entries). A bot with broken settings is skipped. A connector already gone only has its record cleared.
    /// </summary>
    public async Task PruneConnectorsAsync(string bot, BotState state, CancellationToken ct)
    {
        if (cx.Config.IsScopeFrozen(bot)) return;
        var keep = cx.Config.McpEntriesOf(bot).Where(e => e.Url is not null).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var entryName in state.Connectors.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            await DeleteOwnedConnectorAsync(bot, state.Connectors[entryName].Id, ct).ConfigureAwait(false);
            state.Connectors.Remove(entryName);
            cx.SaveManifest();
        }
    }

    /// <summary>
    /// Deleting a bot (after the agent was updated without its scope): deletes every recorded engine-owned connector and, even when the
    /// record was lost, every connector under this bot's name whose owner id is ours.
    /// </summary>
    public async Task DeleteOwnedConnectorsAsync(string bot, BotState state, CancellationToken ct)
    {
        foreach (var entryName in state.Connectors.Keys.ToList())
        {
            await DeleteOwnedConnectorAsync(bot, state.Connectors[entryName].Id, ct).ConfigureAwait(false);
            state.Connectors.Remove(entryName);
            cx.SaveManifest();
        }

        var own = ConnectorNameOf(bot, "");
        var list = await PrefixedConnectorsAsync(ct).ConfigureAwait(false);
        foreach (var leftover in list.Where(r => r.Name.StartsWith(own, StringComparison.Ordinal)
                                                 && OwnerOf(r.Description) == M.Owner).ToList())
        {
            await DeleteOwnedConnectorAsync(bot, leftover.Id, ct).ConfigureAwait(false);
        }
    }

    private async Task DeleteOwnedConnectorAsync(string bot, Guid id, CancellationToken ct)
    {
        var list = await PrefixedConnectorsAsync(ct).ConfigureAwait(false);
        if (list.FirstOrDefault(r => r.Id == id) is not { } remote) return;   // already gone
        if (IsForeign(remote))
        {
            Warn($"Folder chatbot '{bot}': the vault-ai connector '{remote.Name}' belongs to another sync instance (owner {OwnerOf(remote.Description)}), so it is not deleted.");
            return;
        }
        try
        {
            await cx.Client.DeleteConnectorAsync(id, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            // vault-ai can answer a delete of an unknown id with 500 rather than 404 - read the list again and treat a vanished one as deleted.
            var fresh = await cx.Client.ListConnectorsAsync(cx.Prefix, ct).ConfigureAwait(false);
            if (fresh.Any(r => r.Id == id)) throw;
        }
        list.Remove(remote);
        _allConnectors?.RemoveAll(r => r.Id == id);
    }

    private string AgentHash(string instructions, Guid storageId, IReadOnlyList<AgentScope> scopes)
        => Hash([Models.Answer, instructions, storageId,
            scopes.Select(s => new object?[] { s.Name, s.Tags, s.Instructions, s.ConnectorIds }).ToList(),
            // The entry hashes of the engine-owned connectors - an address or header change matches the agent again too.
            scopes.SelectMany(s => M.Bots[s.Name].Connectors.Select(c => s.Name + "/" + c.Key + ":" + c.Value.Hash))
                .Order(StringComparer.Ordinal).ToList()]);

    private static string Hash(object?[] parts)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
}
