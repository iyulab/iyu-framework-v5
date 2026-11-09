using Microsoft.Extensions.Logging;

namespace Iyu.VaultAi.FolderChatbots;

/// <summary>
/// One chatbot as a screen shows it. <see cref="State"/> is one of <c>ready</c>, <c>indexing</c>, <c>error</c>, <c>not_indexable</c>,
/// <c>unsupported</c>, <c>pending_delete</c>, <c>empty</c>, <c>pending_create</c> and <c>unknown</c> (the first that applies, in that order of
/// attention); the counts are what the state is made of. The text to show is the screen's to choose.
/// </summary>
internal sealed record FolderChatbotBotStatus(
    string Name, string State, int Indexed, int Total, int InProgress, int Failed, int NotIndexable, int Skipped);

/// <summary>Reads how each chatbot stands: from the engine's record and vault-ai's summary of the shared storage, tag by tag.</summary>
internal sealed class FolderChatbotStatusReader(
    IFolderChatbotAdmin client, IFolderChatbotSource source, FolderChatbotSyncService service, ILogger<FolderChatbotStatusReader> log)
{
    internal const int Parallelism = 4;

    /// <summary>How long one bot's summary may take before it counts as unknown (settable for tests).</summary>
    internal TimeSpan SummaryTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<FolderChatbotBotStatus>> GetAsync(CancellationToken ct)
    {
        var manifest = service.ManifestSnapshot;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (manifest is not null) names.UnionWith(manifest.Bots.Keys);
        // A folder the engine has not made a chatbot of yet is "pending_create".
        try
        {
            names.UnionWith(source.ListBotFolders());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "The chatbot folders could not be listed for the status");
        }

        // One summary call per bot: in parallel (bounded) and each under a time limit, so that a vault-ai that does not answer holds up
        // one line, not the list.
        var ordered = names.ToArray();
        var result = new FolderChatbotBotStatus[ordered.Length];
        using var cap = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(ordered.Select(async (name, i) =>
        {
            await cap.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                BotState? state = null;
                manifest?.Bots.TryGetValue(name, out state);
                result[i] = await StatusOfAsync(name, state, manifest?.StorageId, ct).ConfigureAwait(false);
            }
            finally { cap.Release(); }
        })).ConfigureAwait(false);
        return result;
    }

    private async Task<FolderChatbotBotStatus> StatusOfAsync(string name, BotState? state, Guid? storage, CancellationToken ct)
    {
        if (state?.PendingDeleteSince is not null) return Of(name, "pending_delete");
        if (state is null || storage is not { } storageId) return Of(name, "pending_create");

        StorageSummary summary;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SummaryTimeout);
            summary = await client.GetStorageSummaryAsync(storageId, [FolderChatbotNames.TagOf(name)], timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A timeout, a failed connection, an answer that cannot be read: this one line is unknown and the list goes on.
            log.LogWarning(ex, "The indexing status of '{Bot}' could not be read", name);
            return Of(name, "unknown");
        }

        int Count(string key) => summary.ByStatus.TryGetValue(key, out var n) ? n : 0;
        var skipped = state.Unsupported.Count;
        var total = summary.TotalCount + skipped;   // skipped files are not in the storage
        var indexed = Count("indexed");
        var inProgress = Count("pending") + Count("indexing") + Count("canceling");
        var failed = Count("failed");
        var notIndexable = Count("not_indexable");
        if (total == 0) return Of(name, "empty");

        var primary = inProgress > 0 ? "indexing" : failed > 0 ? "error" : notIndexable > 0 ? "not_indexable"
            : skipped > 0 ? "unsupported" : "ready";
        return new FolderChatbotBotStatus(name, primary, indexed, total, inProgress, failed, notIndexable, skipped);
    }

    private static FolderChatbotBotStatus Of(string name, string state) => new(name, state, 0, 0, 0, 0, 0, 0);
}
