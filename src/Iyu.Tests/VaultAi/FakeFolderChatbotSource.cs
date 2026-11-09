using System.Security.Cryptography;
using System.Text;
using Iyu.VaultAi.FolderChatbots;

namespace Iyu.Tests.VaultAi;

/// <summary>The folders held in memory: files with content and a write time, and the settings files, for the engine's tests.</summary>
internal sealed class FakeFolderChatbotSource : IFolderChatbotSource
{
    public Dictionary<string, Dictionary<string, (byte[] Content, DateTimeOffset Written)>> Bots { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SettingsText> Agents { get; } = [];
    public Dictionary<string, SettingsText> Mcp { get; } = [];
    public bool Unreadable { get; set; }

    public void Put(string bot, string path, string content, DateTimeOffset? at = null)
    {
        if (!Bots.TryGetValue(bot, out var files)) Bots[bot] = files = new Dictionary<string, (byte[], DateTimeOffset)>(StringComparer.Ordinal);
        files[path] = (Encoding.UTF8.GetBytes(content), at ?? new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    }

    public FolderSnapshot ReadSnapshot(FolderChatbotManifest manifest)
    {
        if (Unreadable) return new FolderSnapshot(false, new Dictionary<string, IReadOnlyList<LocalFile>>(), "share unmounted");
        return new FolderSnapshot(true, Bots.ToDictionary(b => b.Key, b => (IReadOnlyList<LocalFile>)b.Value
            .Select(f => new LocalFile(f.Key, f.Value.Content.Length, f.Value.Written, Convert.ToHexStringLower(SHA256.HashData(f.Value.Content))))
            .ToList(), StringComparer.Ordinal));
    }

    public Stream OpenRead(string bot, string relativePath) => new MemoryStream(Bots[bot][relativePath].Content);

    public SettingsText ReadAgents(string? bot, out string? warning)
    {
        warning = null;
        return Agents.GetValueOrDefault(bot ?? "") ?? new SettingsText(null, null);
    }

    public SettingsText ReadMcp(string bot, out string? warning)
    {
        warning = null;
        return Mcp.GetValueOrDefault(bot) ?? new SettingsText(null, null);
    }
}
