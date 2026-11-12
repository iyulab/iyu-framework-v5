using Microsoft.Extensions.Logging;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>
/// Applies one check to vault-ai. The order: make sure the shared objects exist (<see cref="PrepareAsync"/> - repair, create, read the
/// remote file list) → run the planner's actions (clean up files only the remote has, then each bot's file actions and the file
/// deletes of a bot being removed) → match the agent (scopes, connectors) → clean up the connectors and records of removed bots → match
/// the storage's settings and state meta. One action failing leaves only that item out of the manifest (the next check tries again) and the
/// rest go on. The shared objects are <see cref="FolderChatbotSharedObjects"/>'s, the files <see cref="FolderChatbotFileSync"/>'s.
/// </summary>
internal sealed class FolderChatbotSyncExecutor
{
    /// <summary>The tally key of actions that belong to no bot (cleaning up remote-only files, the shared objects).</summary>
    private const string SharedTally = "";

    private readonly FolderChatbotSyncContext _cx;
    private readonly FolderChatbotSharedObjects _objects;
    private readonly IFolderChatbotSource _source;
    private readonly Action<string>? _deleteCompleted;
    private readonly List<RemoteFile> _remote = [];
    private readonly Dictionary<string, BotTally> _tallies = new(StringComparer.Ordinal);
    private FolderChatbotFileSync? _files;

    /// <param name="deleteCompleted">Called with a bot's name once its deletion is finished, so the host can drop its record of having asked for it. A failure inside it is the host's to handle; the engine ignores one that throws an I/O error.</param>
    public FolderChatbotSyncExecutor(
        IFolderChatbotAdmin client, string prefix, IFolderChatbotSource source, FolderChatbotConfig config,
        FolderChatbotManifest manifest, Action saveManifest, ILogger log, bool adoptForeign = false, long maxFileBytes = 0,
        Action<string>? deleteCompleted = null, bool embeddingExplicit = false)
    {
        _source = source;
        _deleteCompleted = deleteCompleted;
        _cx = new FolderChatbotSyncContext(client, prefix, config, manifest, saveManifest, log)
        {
            AdoptForeign = adoptForeign,
            MaxFileBytes = maxFileBytes,
            EmbeddingExplicit = embeddingExplicit,
        };
        _objects = new FolderChatbotSharedObjects(_cx);
    }

    public int Applied => _tallies.Values.Sum(t => t.Applied);
    public int Failed => _tallies.Values.Sum(t => t.Failed);
    public int Skipped => _tallies.Values.Sum(t => t.Skipped);

    /// <summary>Warnings raised during the check (another instance's storage not taken over, a settings file that cannot be used, …).</summary>
    public IReadOnlyList<string> Warnings => _cx.Warnings;

    private BotTally TallyOf(string bot)
    {
        if (!_tallies.TryGetValue(bot, out var t)) _tallies[bot] = t = new BotTally();
        return t;
    }

    /// <summary>
    /// Makes sure the shared objects exist (repair, create, take over) and reads the shared storage's remote file list. Runs before planning.
    /// If this check cannot go on (the objects look to have vanished all at once, a storage of another instance, a mismatched embedding
    /// model, a failure) it returns a warning. <c>Remote</c> is null when the list could not be read (the planner then trusts the records).
    /// </summary>
    public async Task<(string? Stop, IReadOnlyList<RemoteFile>? Remote)> PrepareAsync(CancellationToken ct)
    {
        try
        {
            if (await _objects.EnsureAsync(ct).ConfigureAwait(false) is { } guard) return (guard, null);
        }
        catch (Exception ex) when (ex is FolderChatbotForeignOwnerException or FolderChatbotEmbeddingMismatchException)
        {
            _cx.Log.LogWarning("{Message}", ex.Message);
            return (ex.Message, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _cx.Log.LogWarning(ex, "Folder chatbot: could not make sure the shared storage, agent and channel exist, so this check is skipped - the next check tries again");
            TallyOf(SharedTally).Failed++;
            return ("Folder chatbot sync is skipped because the shared objects could not be made sure of: " + ex.Message, null);
        }

        try
        {
            _remote.AddRange(await _cx.Client.ListFilesAsync(_cx.Manifest.StorageId!.Value, ct).ConfigureAwait(false));
            _files = new FolderChatbotFileSync(_cx, _source, _remote);
            return (null, _remote.ToList());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Go on trusting the records without a list - only cleaning up remote-only files and re-uploading vanished ones wait for the next check.
            _cx.Log.LogWarning(ex, "Folder chatbot: could not read the shared storage's file list - going on with the records alone");
            _files = new FolderChatbotFileSync(_cx, _source, _remote);
            return (null, null);
        }
    }

    public async Task ExecuteAsync(FolderSnapshot snapshot, IReadOnlyList<SyncAction> actions, CancellationToken ct)
    {
        var files = _files ?? throw new InvalidOperationException("Call PrepareAsync first.");

        foreach (var orphan in actions.OfType<DeleteOrphan>())
        {
            try
            {
                await files.DeleteOrphanAsync(orphan.File, ct).ConfigureAwait(false);
                TallyOf(SharedTally).Applied++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _cx.Log.LogWarning(ex, "Folder chatbot: cleaning up the remote file '{File}' failed - the next check tries again", orphan.File.FileName);
                TallyOf(SharedTally).Failed++;
            }
        }

        var byBot = actions.Where(a => a is not DeleteOrphan).GroupBy(a => a.Bot, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var removing = new HashSet<string>(StringComparer.Ordinal);

        // Tidy the records of refused (415) files - for bots with no action too (if only the refused file was removed, the plan has none),
        // so that the skipped count does not linger.
        foreach (var bot in snapshot.Bots.Keys.Order(StringComparer.Ordinal))
        {
            if (_cx.Manifest.Bots.TryGetValue(bot, out var known)) files.PruneUnsupported(known, snapshot.Bots[bot]);
        }

        foreach (var bot in byBot.Keys.Order(StringComparer.Ordinal))
        {
            var tally = TallyOf(bot);
            try
            {
                if (await RunBotAsync(bot, byBot[bot], files, tally, ct).ConfigureAwait(false)) removing.Add(bot);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _cx.Log.LogError(ex, "Folder chatbot '{Bot}': sync failed", bot);
                tally.Failed++;
            }
        }

        foreach (var bot in snapshot.Bots.Keys.Where(_cx.Manifest.Bots.ContainsKey).Order(StringComparer.Ordinal))
            ReportSettingsIssues(bot);

        // Match the scopes - for bots with no action too (a change to AGENTS.md, mcp.json or the models produces only a settings update).
        var agentReconciled = false;
        try
        {
            await _objects.ReconcileAgentAsync(snapshot, removing, TallyOf, ct).ConfigureAwait(false);
            agentReconciled = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _cx.Log.LogWarning(ex, "Folder chatbot: updating the agent (scopes) failed - the next check tries again");
            TallyOf(SharedTally).Failed++;
        }

        if (agentReconciled)
        {
            // Connectors go only after the agent no longer points at them.
            foreach (var bot in removing.Order(StringComparer.Ordinal))
                await FinishDeleteAsync(bot, ct).ConfigureAwait(false);
            foreach (var bot in snapshot.Bots.Keys.Order(StringComparer.Ordinal))
            {
                if (!_cx.Manifest.Bots.TryGetValue(bot, out var state)) continue;
                try
                {
                    await _objects.PruneConnectorsAsync(bot, state, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _cx.Log.LogWarning(ex, "Folder chatbot '{Bot}': cleaning up connectors failed - the next check tries again", bot);
                    TallyOf(bot).Failed++;
                }
            }
        }

        try
        {
            await _objects.ReconcileStorageAsync(TallyOf, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _cx.Log.LogWarning(ex, "Folder chatbot: updating the storage settings failed");
            TallyOf(SharedTally).Failed++;
        }
    }

    /// <summary>One bot's actions. True once a bot's file deletes are done - its scope, connectors and record are cleaned up after the agent is matched.</summary>
    private async Task<bool> RunBotAsync(string bot, List<SyncAction> actions, FolderChatbotFileSync files, BotTally tally, CancellationToken ct)
    {
        if (actions.OfType<CreateBot>().Any())
        {
            _cx.Manifest.Bots[bot] = new BotState();
            _cx.SaveManifest();
            tally.Applied++;
        }
        if (!_cx.Manifest.Bots.TryGetValue(bot, out var state)) return false;

        if (actions.OfType<DeleteBot>().Any())
        {
            try
            {
                await files.DeleteAllAsync(bot, state, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _cx.Log.LogWarning(ex, "Folder chatbot '{Bot}': deleting it failed - the next check tries again", bot);
                tally.Failed++;
                return false;
            }
        }

        foreach (var action in actions)
        {
            if (action is CreateBot) continue;
            if (_cx.Manifest.StorageId is null) break;   // the storage was lost during this check - the next check's repair picks up
            try
            {
                await files.ApplyAsync(bot, state, action, tally, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _cx.Log.LogWarning(ex, "Folder chatbot '{Bot}': an action failed ({Action}) - the next check tries again", bot, Describe(action));
                tally.Failed++;
            }
        }
        return false;
    }

    /// <summary>After the agent was updated without the bot's scope: delete the engine-owned connectors and take the bot out of the manifest and the host's record of deletions.</summary>
    private async Task FinishDeleteAsync(string bot, CancellationToken ct)
    {
        try
        {
            await _objects.DeleteOwnedConnectorsAsync(bot, _cx.Manifest.Bots[bot], ct).ConfigureAwait(false);
            _cx.Manifest.Bots.Remove(bot);
            _cx.SaveManifest();
            TallyOf(bot).Applied++;
            _cx.Log.LogInformation("Folder chatbot '{Bot}': deleted - its files and scope are gone", bot);
            NotifyDeleted(bot);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _cx.Log.LogWarning(ex, "Folder chatbot '{Bot}': deleting its connectors failed - the next check tries again", bot);
            TallyOf(bot).Failed++;
        }
    }

    /// <summary>The host is told the deletion is finished. A failure in it does not fail the check (the bot to delete is gone already).</summary>
    private void NotifyDeleted(string bot)
    {
        if (_deleteCompleted is null) return;
        try
        {
            _deleteCompleted(bot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _cx.Log.LogWarning(ex, "Folder chatbot: could not clear the record of the requested deletion of '{Bot}'", bot);
        }
    }

    /// <summary>
    /// Raises the errors and warnings of a bot's settings files (AGENTS.md, mcp.json) as warnings of the check. If the instructions could not
    /// be read or mcp.json has an error, the bot's scope stays as last applied until the file is fixed (the connectors are left alone) -
    /// the check as a whole is not stopped.
    /// </summary>
    private void ReportSettingsIssues(string bot)
    {
        const string frozen = " (this bot's scope stays as last applied until the file is fixed - its connectors are left alone)";
        foreach (var issue in _cx.Config.IssuesOf(bot))
        {
            var message = $"Folder chatbot '{bot}' settings: {issue}";
            _cx.Log.LogWarning("{Message}", message + (_cx.Config.IsScopeFrozen(bot) ? frozen : ""));
            _cx.Warnings.Add(message);
        }
        foreach (var issue in _cx.Config.McpOf(bot).Issues)
        {
            var message = $"Folder chatbot '{bot}' mcp.json {(issue.IsWarning ? "warning" : "error")}: {issue.Field} - {issue.Message}";
            _cx.Log.LogWarning("{Message}", message + (issue.IsWarning ? "" : frozen));
            _cx.Warnings.Add(message);
        }
    }

    private static string Describe(SyncAction action) => action switch
    {
        UploadFile u => $"upload {u.File.RelativePath}",
        ReplaceFile r => $"replace {r.File.RelativePath}",
        DeleteFile d => $"delete {d.RelativePath}",
        _ => action.GetType().Name,
    };
}
