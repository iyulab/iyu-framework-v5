namespace Iyu.VaultAi.FolderChatbots;

/// <summary>
/// Where the chatbots' documents come from. The engine does not assume a file system: it asks a source what the folders hold and for the
/// bytes of a file, and decides what to do with the answers. A source also reads the settings files (<see cref="IFolderChatbotSettingsReader"/>).
/// </summary>
internal interface IFolderChatbotSource : IFolderChatbotSettingsReader
{
    /// <summary>
    /// What the folders hold now: one bot per first-level folder, each with its files (paths relative to the bot's folder, <c>/</c>-separated).
    /// Excluded entries and the bots' settings files are left out. Reading must never turn "could not read" into "no files" — a snapshot that
    /// cannot be completed is returned as unreadable (<see cref="FolderSnapshot.RootReadable"/> false), because an absent file is deleted remotely.
    /// <paramref name="manifest"/> lets a source skip hashing a file whose size and modification time still match what was recorded.
    /// </summary>
    FolderSnapshot ReadSnapshot(FolderChatbotManifest manifest);

    /// <summary>The names of the folders that would be bots, cheaply (no file is read). For a status screen; <see cref="ReadSnapshot"/> is what a check uses.</summary>
    IEnumerable<string> ListBotFolders();

    /// <summary>Opens a file for reading. The caller disposes the stream.</summary>
    Stream OpenRead(string bot, string relativePath);
}
