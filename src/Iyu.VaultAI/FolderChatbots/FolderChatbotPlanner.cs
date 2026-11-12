namespace Iyu.VaultAI.FolderChatbots;

/// <summary>
/// Compares the snapshot, the manifest and the shared storage's remote file list, and says what to do. No side effects.
/// </summary>
/// <remarks>
/// <para>
/// With the remote list, the folders and vault-ai are reconciled in both directions: a file the manifest records but that is gone from
/// the remote is uploaded again, and a file only the remote has (not recorded, and not paired with a local file to upload) is deleted.
/// If the remote list could not be read (null), the records are trusted.
/// </para>
/// <para>
/// When a disappearance may be an accident — the root is unreadable, every bot vanished at once, a bot's folder is suddenly empty —
/// nothing is deleted and a warning is returned instead: the cost of waiting a check is small, the cost of deleting a storage's worth of
/// indexed files because a share was unmounted is not.
/// </para>
/// </remarks>
internal static class FolderChatbotPlanner
{
    public static IReadOnlyList<SyncAction> Plan(
        FolderSnapshot fs, FolderChatbotManifest m, IReadOnlyList<RemoteFile>? remote, DateTimeOffset now, TimeSpan grace,
        Func<string, bool> isSupported, IReadOnlySet<string> intendedDeletes, out string? guardWarning)
    {
        guardWarning = null;

        if (!fs.RootReadable)
        {
            guardWarning = "The chatbot folder cannot be read, so this check is skipped (check the path, the share's connection and permissions)"
                           + (fs.UnreadableReason is { } reason ? ": " + reason : ".");
            return [];
        }
        var warnings = new List<string>();
        var actions = new List<SyncAction>();

        if (m.Bots.Count > 0 && fs.Bots.Count == 0 && m.Bots.Keys.Any(b => !intendedDeletes.Contains(b)))
        {
            // If every bot that vanished was deleted on purpose, it is no accident. If only some were, delete those and keep the rest.
            guardWarning = "Every chatbot folder vanished at once, so nothing is deleted and this check is skipped.";
            foreach (var bot in m.Bots.Keys.Where(intendedDeletes.Contains).Order(StringComparer.Ordinal))
                actions.Add(new DeleteBot(bot));
            return actions;
        }

        var remoteIds = remote?.Select(r => r.Id).ToHashSet();
        var guarded = new HashSet<string>(StringComparer.Ordinal); // bots whose deletions were skipped because the folder is empty
        var names = fs.Bots.Keys.Concat(m.Bots.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var bot in names)
        {
            var inFs = fs.Bots.TryGetValue(bot, out var files);
            var inManifest = m.Bots.TryGetValue(bot, out var state);

            if (inFs && !inManifest)
            {
                if (!FolderChatbotNames.IsUsableBotName(bot))
                {
                    warnings.Add($"The bot folder name '{bot}' is longer than {FolderChatbotNames.MaxBotNameLength} characters, so no chatbot is made of it.");
                    continue;
                }
                actions.Add(new CreateBot(bot));
                AddUploads(actions, bot, files!, isSupported);
            }
            else if (inFs)
            {
                if (intendedDeletes.Contains(bot))
                    warnings.Add($"The bot folder '{bot}' exists, so the record of its intended deletion is ignored (the bot is revived).");
                if (!PlanExisting(actions, warnings, bot, files!, state!, remoteIds, isSupported)) guarded.Add(bot);
            }
            else
            {
                PlanMissingFolder(actions, bot, state!, now, grace, intendedDeletes.Contains(bot));
            }
        }

        if (remote is not null) PlanOrphans(actions, fs, m, remote, guarded);

        if (warnings.Count > 0) guardWarning = string.Join(" ", warnings);
        return actions;
    }

    /// <summary>
    /// The stale records of an intended deletion that should be cleared: bots whose folder came back (revived), and bots in neither the
    /// snapshot nor the manifest. A stale record left behind would delete a bot without grace the next time it vanishes by accident, so
    /// it is cleaned up during a check. No side effects (the caller applies it). None when the root could not be read.
    /// </summary>
    public static IReadOnlyList<string> StaleIntents(FolderSnapshot fs, FolderChatbotManifest m, IReadOnlySet<string> intendedDeletes)
    {
        if (!fs.RootReadable) return [];
        return intendedDeletes
            .Where(b => fs.Bots.ContainsKey(b) || !m.Bots.ContainsKey(b))
            .Order(StringComparer.Ordinal).ToList();
    }

    private static void AddUploads(List<SyncAction> actions, string bot, IReadOnlyList<LocalFile> files,
        Func<string, bool> isSupported)
    {
        foreach (var f in files.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
            actions.Add(isSupported(f.RelativePath) ? new UploadFile(bot, f) : new SkipUnsupported(bot, f.RelativePath));
    }

    /// <summary>The file actions of an existing bot. False when deletions were skipped because the folder is empty.</summary>
    private static bool PlanExisting(List<SyncAction> actions, List<string> warnings, string bot,
        IReadOnlyList<LocalFile> files, BotState state, IReadOnlySet<Guid>? remoteIds, Func<string, bool> isSupported)
    {
        if (state.PendingDeleteSince is not null) actions.Add(new ClearPendingDelete(bot));

        if (files.Count == 0 && state.Files.Count > 0)
        {
            // The folder exists but is empty — a failed mount or a bulk delete by accident is possible, so this bot's files are left alone.
            warnings.Add($"The bot folder '{bot}' is empty, so deleting this bot's files is skipped.");
            return false;
        }

        var local = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
        {
            local.Add(f.RelativePath);
            if (!isSupported(f.RelativePath))
            {
                actions.Add(new SkipUnsupported(bot, f.RelativePath));
                continue;
            }
            if (!state.Files.TryGetValue(f.RelativePath, out var known)
                || (remoteIds is not null && !remoteIds.Contains(known.RemoteId))) // recorded but gone from the remote — upload it again
                actions.Add(new UploadFile(bot, f));
            else if (known.Size == f.Size && known.LastWrite == f.LastWrite)
                continue; // the hash is compared only when size or time changed
            else if (string.Equals(known.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase))
                actions.Add(new RefreshFileState(bot, f, known.RemoteId));
            else
                actions.Add(new ReplaceFile(bot, f, known.RemoteId));
        }

        foreach (var (path, known) in state.Files.OrderBy(x => x.Key, StringComparer.Ordinal))
            if (!local.Contains(path))
                actions.Add(new DeleteFile(bot, path, known.RemoteId));
        return true;
    }

    /// <summary>
    /// Deletes the remote files that no bot's record has — except when the folder has a local file at the same path whose path is not
    /// yet recorded against a live remote file: then the file stays and the upload adopts it (an upload whose reply was lost, a lost
    /// manifest, a storage taken over). The remote files of a bot whose deletions were skipped because the folder is empty are not touched.
    /// </summary>
    private static void PlanOrphans(List<SyncAction> actions, FolderSnapshot fs, FolderChatbotManifest m,
        IReadOnlyList<RemoteFile> remote, HashSet<string> guarded)
    {
        var remoteIds = remote.Select(r => r.Id).ToHashSet();
        var tracked = m.Bots.Values.SelectMany(b => b.Files.Values).Select(f => f.RemoteId).ToHashSet();
        foreach (var file in remote.Where(r => !tracked.Contains(r.Id)).OrderBy(r => r.FileName, StringComparer.Ordinal))
        {
            var bot = FolderChatbotNames.BotOf(file);
            if (bot is not null && guarded.Contains(bot)) continue;
            var relative = bot is null ? null : FolderChatbotNames.RelativeOf(bot, file.FileName);
            if (bot is not null && relative is not null && fs.Bots.TryGetValue(bot, out var local)
                && local.Any(f => string.Equals(f.RelativePath, relative, StringComparison.Ordinal)))
            {
                var recorded = m.Bots.TryGetValue(bot, out var state) && state.Files.TryGetValue(relative, out var known)
                               && remoteIds.Contains(known.RemoteId);
                if (!recorded) continue; // the upload adopts it (same size, healthy state) or deletes it
            }
            actions.Add(new DeleteOrphan(bot ?? "", file));
        }
    }

    private static void PlanMissingFolder(List<SyncAction> actions, string bot, BotState state,
        DateTimeOffset now, TimeSpan grace, bool intended)
    {
        if (intended)
            actions.Add(new DeleteBot(bot)); // deleted on purpose from the console — no grace.
        else if (state.PendingDeleteSince is not { } since)
            actions.Add(new MarkPendingDelete(bot, now));
        else if (now - since >= grace)
            actions.Add(new DeleteBot(bot));
        // During the grace period nothing is done.
    }
}
