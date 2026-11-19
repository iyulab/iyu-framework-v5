namespace Iyu.VaultAI;

/// <summary>
/// One chat channel the host allows the browser to embed — an entry of <c>VaultAI:EmbedChannels</c>. The list is the trust
/// boundary of <c>POST {pattern}/channels/{id}/token</c>: the server issues a token with its own key only for a channel named
/// here, so a caller cannot put an arbitrary channel id into that call.
/// </summary>
public sealed class VaultAIEmbedChannel
{
    /// <summary>The chat service's id of the channel. Required; no leading or trailing whitespace; unique in the list.</summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The name a channel picker shows for it, returned by <c>GET {pattern}/channels</c>. Optional: what to show for an entry
    /// without one is the page's decision.
    /// </summary>
    public string? Label { get; set; }
}
