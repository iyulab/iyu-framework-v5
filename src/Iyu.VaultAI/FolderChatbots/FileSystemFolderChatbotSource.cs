using System.Security.Cryptography;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>
/// The folders on a file system as a <see cref="IFolderChatbotSource"/>: each first-level folder under the root is a bot and everything
/// in it, recursively, is its files (paths relative to the bot's folder, <c>/</c>-separated). Built-in exclusions
/// (<see cref="FolderChatbotFolderRules.IsExcluded"/>) and a bot's two settings files directly under its folder are left out; files directly
/// under the root belong to no bot. A file's SHA-256 is computed only when its size or modification time differs from the manifest's record.
/// </summary>
internal sealed class FileSystemFolderChatbotSource(string root) : IFolderChatbotSource
{
    // A link inside a bot's folder (a symbolic link, a junction) is not followed: whoever can write the folder could point it at any
    // file the service account can read, and the engine would upload it to the chatbot (or read it as the bot's instructions).
    // The bot's own folder, made by whoever sets the root up, may itself be a link.
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,   // reading a subfolder that cannot be read as "no files" would delete remotely
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

    public string Root { get; } = root;

    public FolderSnapshot ReadSnapshot(FolderChatbotManifest manifest)
    {
        if (!Directory.Exists(Root))
            return new FolderSnapshot(false, new Dictionary<string, IReadOnlyList<LocalFile>>(), $"the root does not exist - {Root}");

        try
        {
            var bots = new Dictionary<string, IReadOnlyList<LocalFile>>(StringComparer.Ordinal);
            foreach (var botDir in new DirectoryInfo(Root).EnumerateDirectories())
            {
                if (FolderChatbotFolderRules.IsExcluded(botDir.Name)) continue;
                manifest.Bots.TryGetValue(botDir.Name, out var state);
                bots[botDir.Name] = ReadBot(botDir, state);
            }
            return new FolderSnapshot(true, bots);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // One subfolder that cannot be read stops the whole snapshot (leaving it out would delete remotely) - the exception names the path.
            return new FolderSnapshot(false, new Dictionary<string, IReadOnlyList<LocalFile>>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<LocalFile> ReadBot(DirectoryInfo botDir, BotState? state)
    {
        var files = new List<LocalFile>();
        foreach (var file in botDir.EnumerateFiles("*", Recursive))
        {
            if (IsLink(file)) continue;
            var relative = Path.GetRelativePath(botDir.FullName, file.FullName).Replace('\\', '/');
            if (FolderChatbotFolderRules.IsExcluded(relative) || FolderChatbotFolderRules.IsBotSettingsFile(relative)) continue;

            var size = file.Length;
            var lastWrite = new DateTimeOffset(file.LastWriteTimeUtc);
            FileState? known = null;
            state?.Files.TryGetValue(relative, out known);

            if (known is not null && known.Size == size && known.LastWrite == lastWrite)
            {
                files.Add(new LocalFile(relative, size, lastWrite, known.Sha256));
                continue;
            }

            string sha;
            try
            {
                sha = HashFile(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file locked because it is being copied: if it was uploaded already, treat it as unchanged (leaving it out would delete
                // remotely); if it is new, leave it out of this check. It is looked at again next time.
                if (known is not null) files.Add(new LocalFile(relative, known.Size, known.LastWrite, known.Sha256));
                continue;
            }
            files.Add(new LocalFile(relative, size, lastWrite, sha));
        }
        return files;
    }

    // Write sharing is not allowed: a file another process is writing fails to open and is left out of this check.
    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public IEnumerable<string> ListBotFolders()
        => Directory.Exists(Root)
            ? new DirectoryInfo(Root).EnumerateDirectories().Select(d => d.Name)
                .Where(n => !FolderChatbotFolderRules.IsExcluded(n)).ToList()
            : [];

    /// <summary>Test seams: run between the steps of <see cref="OpenRead"/> so a test can stage a swap at exactly that instant.</summary>
    internal Action? AfterFirstLinkCheck { get; set; }

    /// <inheritdoc cref="AfterFirstLinkCheck"/>
    internal Action? AfterOpen { get; set; }

    /// <summary>
    /// Opens a file of a bot. The file has to be inside the bot's folder and reached without crossing a link: a path that was an ordinary
    /// file when the folder was scanned may have been replaced by a link since. The path is checked before the file is opened; the open
    /// file is then checked twice more - by its path again (a link that is still there), and by asking the operating system where the open
    /// handle really is (a link put in place for the instant of the open and taken out again). A file that fails either is closed and refused.
    /// </summary>
    /// <remarks>
    /// The last check is exact on Windows and Linux. Where the operating system cannot say where an open handle is (macOS) only the path
    /// checks remain, which close the ordinary swap but not a determined race; there, and wherever the people who can write a bot's
    /// folder are not trusted with whatever the service account can read, serve the documents from a location they cannot make links in.
    /// </remarks>
    public Stream OpenRead(string bot, string relativePath)
    {
        var botDir = Path.GetFullPath(Path.Combine(Root, bot));
        var full = Path.GetFullPath(Path.Combine(botDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var inside = botDir.EndsWith(Path.DirectorySeparatorChar) ? botDir : botDir + Path.DirectorySeparatorChar;
        if (!full.StartsWith(inside, StringComparison.Ordinal))
            throw new IOException($"The path '{relativePath}' leaves the folder of the bot '{bot}'.");

        ThrowIfThroughLink(botDir, full, bot, relativePath);
        AfterFirstLinkCheck?.Invoke();
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            AfterOpen?.Invoke();
            ThrowIfThroughLink(botDir, full, bot, relativePath);   // again, now that the file is open
            FolderChatbotRealPath.ThrowIfOutside(stream, botDir, $"The path '{relativePath}' of the bot '{bot}'");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Throws if the file, or any folder between it and the bot's folder, is a link.</summary>
    private static void ThrowIfThroughLink(string botDir, string full, string bot, string relativePath)
    {
        var stop = botDir.TrimEnd(Path.DirectorySeparatorChar);
        for (var current = new FileInfo(full) as FileSystemInfo; current is not null;
             current = (current as FileInfo)?.Directory ?? (current as DirectoryInfo)?.Parent)
        {
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.Ordinal)) break;
            if (current.Exists && IsLink(current))
                throw new IOException($"The path '{relativePath}' of the bot '{bot}' goes through a link, which is not followed.");
        }
    }

    // ---- settings files

    public SettingsText ReadAgents(string? bot, out string? warning)
        => ReadText(bot is null ? Root : Path.Combine(Root, bot), FolderChatbotFolderRules.AgentsFileName, trim: true, out warning);

    public SettingsText ReadMcp(string bot, out string? warning)
        => ReadText(Path.Combine(Root, bot), FolderChatbotFolderRules.McpFileName, trim: false, out warning);

    private SettingsText ReadText(string dir, string fileName, bool trim, out string? warning)
    {
        string? path;
        try
        {
            path = FindSettingsFile(dir, fileName, out warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = null;
            return new SettingsText(null, "cannot be read: " + ex.Message);
        }
        if (path is null) return new SettingsText(null, null);
        // A settings file that is a link is not read: it would turn any file the service can read into the bot's instructions.
        if (IsLink(new FileInfo(path))) return new SettingsText(null, "is a link, which is not followed");
        try
        {
            // Opened first and checked again - by path and by where the open handle really is (see OpenRead) - so a link put in place
            // after the first look is not read.
            AfterFirstLinkCheck?.Invoke();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            AfterOpen?.Invoke();
            if (IsLink(new FileInfo(path))) return new SettingsText(null, "is a link, which is not followed");
            FolderChatbotRealPath.ThrowIfOutside(stream, dir, $"'{fileName}'");
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (trim) text = text.Trim();
            return new SettingsText(string.IsNullOrWhiteSpace(text) ? null : text, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsText(null, "cannot be read: " + ex.Message);
        }
    }

    /// <summary>
    /// The file directly under <paramref name="dir"/> whose name equals <paramref name="fileName"/> ignoring case; null if there is none (or no
    /// folder). If several differ only by case (a case-sensitive file system), the exact spelling is used, else the first in ordinal order, and
    /// <paramref name="warning"/> says so.
    /// </summary>
    internal static string? FindSettingsFile(string dir, string fileName, out string? warning)
    {
        warning = null;
        if (!Directory.Exists(dir)) return null;
        var matches = new DirectoryInfo(dir).EnumerateFiles()
            .Where(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name).Order(StringComparer.Ordinal).ToList();
        if (matches.Count == 0) return null;
        var pick = matches.Contains(fileName) ? fileName : matches[0];
        if (matches.Count > 1)
            warning = $"Several {fileName} files differ only by case, so '{pick}' is used: {string.Join(", ", matches)}";
        return Path.Combine(dir, pick);
    }
}
