namespace Iyu.VaultAI.FolderChatbots;

/// <summary>One file kept in a vault-ai storage. <see cref="FileName"/> is the relative path it was uploaded under; <see cref="Tags"/> are its file tags.</summary>
internal sealed record RemoteFile(Guid Id, string FileName, long ContentSize, string Status)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// The names a folder chatbot writes on the vault-ai side. These are a <b>stored data format</b>, not settings: an installation
/// that already runs folder chatbots has storages, agents, channels, files and tags named exactly like this, and a new engine
/// that spelled any of them differently would see every existing file as foreign, delete it after the grace period and upload
/// it again (a full re-embedding). The values are therefore fixed by what is already in vault-ai.
/// </summary>
/// <remarks>
/// All bots share one storage, one agent and one web channel, named <see cref="SharedName"/>; one bot is one scope of that
/// agent. The scope's name is the bot's folder name, and the files a scope searches are the ones tagged <see cref="TagOf"/>.
/// </remarks>
internal static class FolderChatbotNames
{
    /// <summary>The part of the shared objects' names that follows the prefix.</summary>
    public const string SharedSuffix = "all";

    /// <summary>The front of a bot's file tag.</summary>
    public const string TagPrefix = "bot:";

    /// <summary>vault-ai limits a file tag to 100 characters; this is what is left for the bot's folder name after <see cref="TagPrefix"/>.</summary>
    public const int MaxBotNameLength = 100 - 4;

    /// <summary>The name of the storage, the agent and the web channel: <c>{prefix}all</c>.</summary>
    public static string SharedName(string prefix) => prefix + SharedSuffix;

    /// <summary>A bot's file tag: <c>bot:{bot}</c>.</summary>
    public static string TagOf(string bot) => TagPrefix + bot;

    /// <summary>The file's name on the vault-ai side: <c>{bot}/{path relative to the bot's folder}</c>.</summary>
    public static string RemoteFileName(string bot, string relativePath) => bot + "/" + relativePath;

    /// <summary>The bot a remote file belongs to — its <c>bot:…</c> tag if it has one, else the first segment of its name; null when neither says.</summary>
    public static string? BotOf(RemoteFile file)
    {
        foreach (var tag in file.Tags)
            if (tag.StartsWith(TagPrefix, StringComparison.Ordinal) && tag.Length > TagPrefix.Length)
                return tag[TagPrefix.Length..];
        var slash = file.FileName.IndexOf('/');
        return slash > 0 ? file.FileName[..slash] : null;
    }

    /// <summary>The path relative to the bot's folder, from a remote file name; null when the name does not start with <c>{bot}/</c>.</summary>
    public static string? RelativeOf(string bot, string remoteFileName)
        => remoteFileName.StartsWith(bot + "/", StringComparison.Ordinal) ? remoteFileName[(bot.Length + 1)..] : null;

    /// <summary>Whether a bot's folder name can serve as a tag and a scope name (its length).</summary>
    public static bool IsUsableBotName(string bot) => bot.Length is > 0 and <= MaxBotNameLength;
}
