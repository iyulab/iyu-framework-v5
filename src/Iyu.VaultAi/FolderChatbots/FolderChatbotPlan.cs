namespace Iyu.VaultAi.FolderChatbots;

/// <summary>One file as the folder source sees it. <see cref="RelativePath"/> is relative to the bot's folder, <c>/</c>-separated.</summary>
internal sealed record LocalFile(string RelativePath, long Size, DateTimeOffset LastWrite, string Sha256);

/// <summary>
/// What the folders looked like in one check. Excluded entries and the bots' settings files are already left out when the snapshot is
/// made. Keyed by the bot's folder name. <see cref="UnreadableReason"/> says, when <see cref="RootReadable"/> is false, what could not
/// be read and why (for the warning).
/// </summary>
internal sealed record FolderSnapshot(
    bool RootReadable, IReadOnlyDictionary<string, IReadOnlyList<LocalFile>> Bots, string? UnreadableReason = null);

internal abstract record SyncAction(string Bot);

/// <summary>A new bot — recorded in the manifest (it becomes a scope of the shared agent).</summary>
internal sealed record CreateBot(string Bot) : SyncAction(Bot);

internal sealed record UploadFile(string Bot, LocalFile File) : SyncAction(Bot);

/// <summary>The content changed — the old file is deleted and the new one uploaded.</summary>
internal sealed record ReplaceFile(string Bot, LocalFile File, Guid OldRemoteId) : SyncAction(Bot);

/// <summary>Only size or time changed and the content is the same — only the manifest is updated (no re-upload).</summary>
internal sealed record RefreshFileState(string Bot, LocalFile File, Guid RemoteId) : SyncAction(Bot);

internal sealed record DeleteFile(string Bot, string RelativePath, Guid RemoteId) : SyncAction(Bot);

internal sealed record MarkPendingDelete(string Bot, DateTimeOffset Since) : SyncAction(Bot);

internal sealed record ClearPendingDelete(string Bot) : SyncAction(Bot);

/// <summary>Delete the bot — every file tagged <c>bot:{bot}</c> and its scope.</summary>
internal sealed record DeleteBot(string Bot) : SyncAction(Bot);

internal sealed record SkipUnsupported(string Bot, string RelativePath) : SyncAction(Bot);

/// <summary>
/// A remote file in the shared storage that no bot's record has and that pairs with no local file to upload — deleted.
/// <see cref="SyncAction.Bot"/> is the remote file's bot (empty when unknown).
/// </summary>
internal sealed record DeleteOrphan(string Bot, RemoteFile File) : SyncAction(Bot);
