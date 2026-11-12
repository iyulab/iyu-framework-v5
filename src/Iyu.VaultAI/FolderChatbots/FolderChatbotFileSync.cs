using Microsoft.Extensions.Logging;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>
/// File-level actions (upload, replace, delete, refresh a record) applied to the shared storage. A file's name on the vault-ai side is
/// <c>{bot}/{relative path}</c> and its tag is <c>bot:{bot}</c> (<see cref="FolderChatbotNames"/>).
/// Before uploading a path the manifest does not record, the engine looks at the remote file list read at the start of the check and, if a
/// file of the same name and size is already there, adopts it instead of uploading: an upload the server took but whose answer was lost
/// (timeout, dropped connection, interruption), a lost manifest, or a storage taken over should not upload everything again.
/// </summary>
internal sealed class FolderChatbotFileSync(FolderChatbotSyncContext cx, IFolderChatbotSource source, List<RemoteFile> remote)
{
    public async Task ApplyAsync(string bot, BotState state, SyncAction action, BotTally tally, CancellationToken ct)
    {
        switch (action)
        {
            case SkipUnsupported:
                tally.Skipped++;
                return;

            case UploadFile u:
                if (IsKnownUnsupported(state, u.File)) { tally.Skipped++; return; }
                // A file the manifest records but that has vanished remotely: clear the record first (the remote is already gone).
                if (state.Files.TryGetValue(u.File.RelativePath, out var stale))
                    await ForgetRemoteFileAsync(state, u.File.RelativePath, stale.RemoteId, ct).ConfigureAwait(false);
                await UploadOrAdoptAsync(bot, state, u.File, tally, ct).ConfigureAwait(false);
                return;

            case ReplaceFile r:
                if (IsKnownUnsupported(state, r.File)) { tally.Skipped++; return; }
                // The old file goes first - on 409 (being indexed) nothing is changed and the next check tries again.
                await ForgetRemoteFileAsync(state, r.File.RelativePath, r.OldRemoteId, ct).ConfigureAwait(false);
                await UploadOrAdoptAsync(bot, state, r.File, tally, ct).ConfigureAwait(false);
                return;

            case RefreshFileState s:
                state.Files[s.File.RelativePath] = new FileState(s.RemoteId, s.File.Size, s.File.LastWrite, s.File.Sha256);
                cx.SaveManifest();
                tally.Applied++;
                return;

            case DeleteFile d:
                await ForgetRemoteFileAsync(state, d.RelativePath, d.RemoteId, ct).ConfigureAwait(false);
                tally.Applied++;
                return;

            case MarkPendingDelete m:
                state.PendingDeleteSince = m.Since;
                cx.SaveManifest();
                tally.Applied++;
                return;

            case ClearPendingDelete:
                state.PendingDeleteSince = null;
                cx.SaveManifest();
                tally.Applied++;
                return;

            default:
                throw new InvalidOperationException($"No handler for the action {action.GetType().Name}.");
        }
    }

    /// <summary>Deletes a remote file no bot's record has (a file already gone is fine).</summary>
    public async Task DeleteOrphanAsync(RemoteFile file, CancellationToken ct)
    {
        await DeleteRemoteFileAsync(file.Id, ct).ConfigureAwait(false);
        remote.RemoveAll(r => r.Id == file.Id);
    }

    /// <summary>
    /// Deletes all of a bot's files: the recorded ones and, as a safety net for a lost record, the remote files carrying the bot's tag.
    /// Throws if any one fails (what was deleted is out of the record, so the next check deletes only what is left).
    /// </summary>
    public async Task DeleteAllAsync(string bot, BotState state, CancellationToken ct)
    {
        foreach (var (path, file) in state.Files.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
            await ForgetRemoteFileAsync(state, path, file.RemoteId, ct).ConfigureAwait(false);

        var tag = FolderChatbotNames.TagOf(bot);
        foreach (var leftover in remote.Where(r => r.Tags.Contains(tag, StringComparer.Ordinal)).ToList())
            await DeleteOrphanAsync(leftover, ct).ConfigureAwait(false);
    }

    /// <summary>Drops the records of files the server refused as an unsupported kind (415) that are gone now or whose content changed (a change tries again).</summary>
    public void PruneUnsupported(BotState state, IReadOnlyList<LocalFile> files)
    {
        if (state.Unsupported.Count == 0) return;
        var current = files.ToDictionary(f => f.RelativePath, f => f.Sha256, StringComparer.Ordinal);
        var stale = state.Unsupported
            .Where(kv => !current.TryGetValue(kv.Key, out var sha) || !string.Equals(sha, kv.Value, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();
        if (stale.Count == 0) return;
        foreach (var path in stale) state.Unsupported.Remove(path);
        cx.SaveManifest();
    }

    private static bool IsKnownUnsupported(BotState state, LocalFile file)
        => state.Unsupported.TryGetValue(file.RelativePath, out var sha)
           && string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Uploads a path the manifest does not record. If an untracked remote file of the same name and size is in a good state it is adopted
    /// (no upload) and any other untracked copies of the name (failed ones included) are deleted. A file larger than
    /// <see cref="FolderChatbotSyncContext.MaxFileBytes"/> is skipped (looked at again every check - raising the limit uploads it).
    /// A limit of this: the remote content hash is not known, so only name and size are compared - a copy of the same size but other
    /// content is adopted too.
    /// </summary>
    private async Task UploadOrAdoptAsync(string bot, BotState state, LocalFile file, BotTally tally, CancellationToken ct)
    {
        var storageId = cx.Manifest.StorageId!.Value;
        var remoteName = FolderChatbotNames.RemoteFileName(bot, file.RelativePath);
        var tracked = cx.Manifest.Bots.Values.SelectMany(b => b.Files.Values).Select(f => f.RemoteId).ToHashSet();
        var sameName = remote
            .Where(r => string.Equals(r.FileName, remoteName, StringComparison.Ordinal) && !tracked.Contains(r.Id))
            .ToList();
        var adopt = sameName.FirstOrDefault(r => r.ContentSize == file.Size && IsAdoptable(r.Status));

        foreach (var extra in sameName.Where(r => r != adopt))
            await DeleteOrphanAsync(extra, ct).ConfigureAwait(false);

        if (adopt is not null)
        {
            cx.Log.LogInformation("Folder chatbot '{Bot}': the file '{File}' is already in vault-ai, so it is recorded instead of uploaded", bot, file.RelativePath);
            Record(state, file, adopt.Id);
            tally.Applied++;
            return;
        }

        if (cx.MaxFileBytes > 0 && file.Size > cx.MaxFileBytes)
        {
            cx.Log.LogInformation("Folder chatbot '{Bot}': the file '{File}' is over the size limit ({Limit} MB) and is skipped", bot, file.RelativePath,
                cx.MaxFileBytes / (1024 * 1024));
            tally.Skipped++;
            return;
        }

        var tags = new[] { FolderChatbotNames.TagOf(bot) };
        Guid id;
        try
        {
            // The client closes the stream once sent; owning it here too closes it if anything fails before that (closing twice is harmless).
            await using var stream = source.OpenRead(bot, file.RelativePath);
            id = await cx.Client.UploadFileAsync(storageId, remoteName, tags, stream, ct).ConfigureAwait(false);
        }
        catch (VaultAIUnsupportedFileException ex)
        {
            // The same content is not sent again (a change makes PruneUnsupported drop the record, and it is tried once more).
            // 413 (over the server's upload limit) is the same - after raising the server limit, delete the manifest or change the file.
            cx.Log.LogInformation("Folder chatbot '{Bot}': the server did not take the file '{File}' ({Reason}) and it is skipped: {Message}",
                bot, file.RelativePath,
                ex.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge ? "over the size limit" : "unsupported kind", ex.Message);
            state.Unsupported[file.RelativePath] = file.Sha256;
            cx.SaveManifest();
            tally.Skipped++;
            return;
        }
        catch (Exception ex) when (FolderChatbotSyncContext.IsNotFound(ex))
        {
            // The storage has vanished remotely - the next check's repair makes a new one and uploads the files again.
            FolderChatbotSyncContext.ForgetStorage(cx.Manifest);
            cx.SaveManifest();
            throw;
        }
        remote.Add(new RemoteFile(id, remoteName, file.Size, "pending") { Tags = tags });
        Record(state, file, id);
        tally.Applied++;
    }

    /// <summary>The remote file states worth adopting: indexed, or waiting or being indexed. A failed, unindexable or cancelled copy is replaced.</summary>
    public static bool IsAdoptable(string status) => status is "indexed" or "pending" or "indexing";

    private void Record(BotState state, LocalFile file, Guid remoteId)
    {
        state.Files[file.RelativePath] = new FileState(remoteId, file.Size, file.LastWrite, file.Sha256);
        state.Unsupported.Remove(file.RelativePath);
        cx.SaveManifest();
    }

    /// <summary>Deletes a remote file (one already gone is fine) and takes it out of the manifest. Any other failure is thrown, with the manifest unchanged.</summary>
    private async Task ForgetRemoteFileAsync(BotState state, string relativePath, Guid remoteId, CancellationToken ct)
    {
        await DeleteRemoteFileAsync(remoteId, ct).ConfigureAwait(false);
        remote.RemoveAll(r => r.Id == remoteId);
        state.Files.Remove(relativePath);
        cx.SaveManifest();
    }

    private async Task DeleteRemoteFileAsync(Guid fileId, CancellationToken ct)
    {
        try
        {
            await cx.Client.DeleteFileAsync(cx.Manifest.StorageId!.Value, fileId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (FolderChatbotSyncContext.IsNotFound(ex))
        {
            // Already gone.
        }
    }
}
