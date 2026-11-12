using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>Where the engine keeps its record (the manifest) between checks. The host decides the place; the engine only reads and writes JSON.</summary>
internal interface IFolderChatbotManifestStore
{
    /// <summary>The stored JSON; null when there is none yet.</summary>
    string? Read();

    /// <summary>Replaces the stored JSON. A write must not leave a half-written record behind.</summary>
    void Write(string json);

    /// <summary>Keeps a copy of what is stored now before it is discarded (a manifest written for another prefix or address).</summary>
    void Backup();
}

/// <summary>A manifest kept in a file, written to a temporary file first and then moved over it.</summary>
internal sealed class FileFolderChatbotManifestStore(string path) : IFolderChatbotManifestStore
{
    public string Path { get; } = path;

    public string? Read() => File.Exists(Path) ? File.ReadAllText(Path) : null;

    public void Write(string json)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public void Backup()
    {
        if (File.Exists(Path)) File.Copy(Path, Path + ".bak", overwrite: true);
    }
}

/// <summary>What one check did. <see cref="Skipped"/> counts files not uploaded because of their kind or size (not a failure).</summary>
internal sealed record FolderChatbotSyncReport(int Applied, int Failed, string? GuardWarning)
{
    public int Skipped { get; init; }
}

/// <summary>
/// One check of the folders against vault-ai, and the state it keeps between checks. A check runs one at a time. The record of what was
/// uploaded (the manifest) is bound to the prefix and the vault-ai address it was written for: if either changes it is set aside (a copy is
/// kept) and the engine starts afresh, so that objects of another prefix or server are never touched with an old id. The engine's owner id
/// is kept across that (it is the same instance).
/// </summary>
internal sealed class FolderChatbotSyncService
{
    private readonly VaultAIFolderChatbotOptions _options;
    private readonly IFolderChatbotAdmin _client;
    private readonly IFolderChatbotSource _source;
    private readonly IFolderChatbotManifestStore _store;
    private readonly string _vaultUrl;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _grace;
    private readonly Func<IReadOnlySet<string>>? _intendedDeletes;
    private readonly Action<string>? _deleteCompleted;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile FolderChatbotManifest? _snapshot;
    private volatile ModelPick? _lastPick;

    /// <param name="intendedDeletes">Bots the host knows were deleted on purpose (they skip the safety nets and the grace period). Read at the start of every check.</param>
    /// <param name="deleteCompleted">Called with a bot's name when its deletion has finished.</param>
    public FolderChatbotSyncService(
        VaultAIFolderChatbotOptions options, IFolderChatbotAdmin client, IFolderChatbotSource source,
        IFolderChatbotManifestStore store, string vaultUrl, ILogger log, TimeProvider? time = null,
        Func<IReadOnlySet<string>>? intendedDeletes = null, Action<string>? deleteCompleted = null)
    {
        _options = options;
        _client = client;
        _source = source;
        _store = store;
        _vaultUrl = vaultUrl;
        _log = log;
        _time = time ?? TimeProvider.System;
        _intendedDeletes = intendedDeletes;
        _deleteCompleted = deleteCompleted;
        if (options.DeleteGraceHours < 1)
        {
            _log.LogWarning("DeleteGraceHours={Hours} is too short and counts as 1 hour (so that a folder that vanishes for a moment does not delete a chatbot).",
                options.DeleteGraceHours);
        }
        _grace = TimeSpan.FromHours(Math.Max(1, options.DeleteGraceHours));
    }

    /// <summary>The models the last check picked and its warnings; null before the first check or when the model list could not be read.</summary>
    public ModelPick? Models => _lastPick;

    /// <summary>A copy of the last stored manifest, for readers that must not wait for a check. Null before the first check.</summary>
    public FolderChatbotManifest? ManifestSnapshot => _snapshot;

    public async Task<FolderChatbotSyncReport> RunOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunOnceCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SaveManifest(FolderChatbotManifest manifest)
    {
        _store.Write(manifest.ToJson());
        _snapshot = manifest.Clone();
    }

    private async Task<FolderChatbotSyncReport> RunOnceCoreAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Path))
            return Skip("The chatbot folder path is not set, so folder chatbot sync is skipped.");
        if (string.IsNullOrWhiteSpace(_options.Prefix))
            return Skip("The prefix is not set, so folder chatbot sync is skipped (it is required so that objects the engine did not make are never touched).");

        FolderChatbotManifest manifest;
        try
        {
            manifest = LoadManifest();
            _snapshot = manifest.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "The folder chatbot manifest could not be read, so sync is skipped. Delete the file to rebuild it from what vault-ai holds");
            return new FolderChatbotSyncReport(0, 0, "The manifest could not be read, so sync is skipped: " + ex.Message);
        }

        // Another instance's storage is taken over only when the manifest records no shared storage (a new start). Decided once here, so that
        // taking over the storage during a check does not make its connectors be refused later in the same check.
        var adoptForeign = _options.AdoptForeign && manifest.StorageId is null;

        // Files uploaded before a model exists harden into "cannot be indexed", so nothing is done until the index, answer and embedding models are all decided.
        ModelPick pick;
        try
        {
            pick = await PickModelsAsync(manifest, adoptForeign, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "The vault-ai model list could not be read, so folder chatbot sync is skipped - the next check tries again");
            return new FolderChatbotSyncReport(0, 0, "The vault-ai model list could not be read, so sync is skipped: " + ex.Message);
        }
        _lastPick = pick;
        var modelWarning = pick.Warnings.Count == 0 ? null : string.Join(" ", pick.Warnings);
        if (!pick.Models.IsComplete)
        {
            var message = "The models for the chatbots could not be decided, so sync is skipped: " + modelWarning;
            _log.LogError("{Message}", message);
            return new FolderChatbotSyncReport(0, 0, message);
        }
        foreach (var warning in pick.Warnings) _log.LogWarning("{Message}", warning);
        if (manifest.Models != pick.Models)
        {
            if (manifest.Models is { } before)
            {
                _log.LogWarning("Changing the chatbot models: index {OldIndex} -> {Index}, answer {OldAnswer} -> {Answer}, embedding {OldEmbedding} -> {Embedding}, rerank {OldRerank} -> {Rerank}",
                    before.Index, pick.Models.Index, before.Answer, pick.Models.Answer, before.Embedding, pick.Models.Embedding,
                    before.Rerank, pick.Models.Rerank);
            }
            manifest.Models = pick.Models;
            SaveManifest(manifest);
        }

        var hosts = _options.McpAllowedHosts is null ? null : new HashSet<string>(_options.McpAllowedHosts, StringComparer.OrdinalIgnoreCase);
        if (hosts is null)
        {
            _log.LogInformation("Folder chatbots: no MCP host restriction is set, so an mcp.json address may point at any host. Set the allowed hosts to restrict where a secret reference can be sent");
        }
        var config = new FolderChatbotConfig(_source, pick.Models, hosts);
        foreach (var issue in config.RootIssues)
            _log.LogWarning("Folder chatbot common settings: {Issue} (if it could not be read, the agent's instructions stay as last applied until it is fixed)", issue);

        var intended = _intendedDeletes?.Invoke() ?? new HashSet<string>();
        var snapshot = _source.ReadSnapshot(manifest);
        var executor = new FolderChatbotSyncExecutor(_client, _options.Prefix, _source, config, manifest,
            () => SaveManifest(manifest), _log, adoptForeign,
            _options.MaxFileMB > 0 ? _options.MaxFileMB * 1024L * 1024L : 0, _deleteCompleted, pick.EmbeddingExplicit);

        if (!snapshot.RootReadable)
        {
            FolderChatbotPlanner.Plan(snapshot, manifest, null, _time.GetUtcNow(), _grace, FolderChatbotSupportedFiles.IsSupported,
                intended, out var unreadable);
            _log.LogWarning("Folder chatbot safety net: {Warning}", unreadable);
            return new FolderChatbotSyncReport(0, 0, unreadable);
        }

        // The shared objects first (repaired if they vanished) - a lost storage empties the file records, and the plan below uploads again.
        var (stop, remote) = await executor.PrepareAsync(ct).ConfigureAwait(false);
        if (stop is not null)
        {
            _log.LogWarning("Folder chatbot safety net: {Warning}", stop);
            return new FolderChatbotSyncReport(executor.Applied, executor.Failed, stop);
        }

        var actions = FolderChatbotPlanner.Plan(snapshot, manifest, remote, _time.GetUtcNow(), _grace,
            FolderChatbotSupportedFiles.IsSupported, intended, out var guard);
        foreach (var issue in config.RootIssues) guard = guard is null ? issue : guard + " " + issue;
        if (guard is not null) _log.LogWarning("Folder chatbot safety net: {Warning}", guard);

        await executor.ExecuteAsync(snapshot, actions, ct).ConfigureAwait(false);

        _snapshot = manifest.Clone();   // the picture at the end of the check, even if some change was not stored
        if (executor.Applied + executor.Failed + executor.Skipped > 0)
        {
            _log.LogInformation("Folder chatbot sync: applied {Applied}, failed {Failed}, skipped {Skipped}",
                executor.Applied, executor.Failed, executor.Skipped);
        }
        foreach (var warning in executor.Warnings.Prepend(modelWarning).OfType<string>())
            guard = guard is null ? warning : guard + " " + warning;
        return new FolderChatbotSyncReport(executor.Applied, executor.Failed, guard) { Skipped = executor.Skipped };

        FolderChatbotSyncReport Skip(string message)
        {
            _log.LogWarning("{Message}", message);
            return new FolderChatbotSyncReport(0, 0, message);
        }
    }

    /// <summary>
    /// The models of this check. "The models already in use" are those the manifest records; with no record (a first start, or a new
    /// manifest) they are read from the shared storage ({prefix}all, if it may be taken over) - so that losing the manifest never switches
    /// the embedding model that is in use.
    /// </summary>
    private async Task<ModelPick> PickModelsAsync(FolderChatbotManifest manifest, bool adoptForeign, CancellationToken ct)
    {
        var models = await _client.ListModelsAsync(ct).ConfigureAwait(false);
        var inUse = manifest.Models;
        if (inUse is null)
        {
            var name = FolderChatbotNames.SharedName(_options.Prefix);
            var shared = (await _client.ListStoragesAsync(_options.Prefix, ct).ConfigureAwait(false))
                .FirstOrDefault(st => string.Equals(st.Name, name, StringComparison.Ordinal)
                                      && (adoptForeign || !IsForeign(st, manifest.Owner)));
            inUse = new FolderChatbotModels(shared?.LanguageModel, null, shared?.EmbeddingModel, shared?.RerankModel);
        }
        return FolderChatbotModelPicker.Pick(_options.Models ?? new VaultAIFolderChatbotModelOptions(), inUse, models);

        static bool IsForeign(NamedRef storage, Guid owner)
            => FolderChatbotStorageMeta.TryParse(storage.Description, out var meta) && meta!.Owner is { } o && o != owner;
    }

    /// <summary>
    /// Reads the manifest and binds it to this configuration. An owner id is made if there is none. A manifest written for another prefix or
    /// vault-ai address is set aside (a copy is kept) and a fresh one started - the old ids are never used against another prefix or server,
    /// and only objects under the new prefix are found by name; the owner id carries over. A new owner id, or a changed binding, is stored at
    /// once: if the owner id reached the remote storage and the engine stopped before storing it, the next check would make another id and
    /// see its own storage as another instance's.
    /// </summary>
    private FolderChatbotManifest LoadManifest()
    {
        var manifest = FolderChatbotManifest.Parse(_store.Read());
        var url = NormalizeUrl(_vaultUrl);
        var prefixChanged = manifest.Prefix is not null
                            && !string.Equals(manifest.Prefix, _options.Prefix, StringComparison.Ordinal);
        var urlChanged = manifest.Url is not null
                         && !string.Equals(NormalizeUrl(manifest.Url), url, StringComparison.OrdinalIgnoreCase);
        var changed = false;

        if (prefixChanged || urlChanged)
        {
            _log.LogWarning(
                "The folder chatbot manifest was written for other settings (prefix '{OldPrefix}' -> '{Prefix}', address '{OldUrl}' -> '{Url}'). " +
                "A copy of the old one is kept and a new one started - objects under the old prefix or on the old server are not touched; delete them in vault-ai if they are not wanted.",
                manifest.Prefix, _options.Prefix, manifest.Url, url);
            _store.Backup();
            manifest = new FolderChatbotManifest { Owner = manifest.Owner };
            changed = true;
        }
        if (manifest.Owner == Guid.Empty)
        {
            manifest.Owner = Guid.NewGuid();
            changed = true;
        }
        if (manifest.Prefix is null || manifest.Url is null)
        {
            manifest.Prefix = _options.Prefix;
            manifest.Url = url;
            changed = true;
        }
        if (changed) _store.Write(manifest.ToJson());
        return manifest;
    }

    private static string NormalizeUrl(string? url) => (url ?? "").Trim().TrimEnd('/');
}
