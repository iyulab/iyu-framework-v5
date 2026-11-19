namespace Iyu.UBoard;

/// <summary>
/// The model service an embedded assistant talks to: any service that speaks the OpenAI chat-completions wire (a hosted one, a
/// gateway in front of another provider, or a model served locally). The key stays on the server — the browser talks to the
/// host's proxy route, never to the model service.
/// </summary>
/// <remarks>
/// The library reads nothing from the environment; the host passes the values it keeps (configuration, a secret store). A value
/// that is missing stops the host at start, and the message names what is missing, never a value.
/// </remarks>
public sealed class IyuUBoardModelOptions
{
    /// <summary>
    /// The service's base address, the part before <c>/chat/completions</c> (for example <c>https://api.example.test/v1</c>).
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>The key the service wants as a bearer token. It is sent from the server only and is never logged or returned.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The model every request is sent to. The browser cannot choose it: a request that names another model is sent to this one.
    /// </summary>
    public string? ModelName { get; set; }

    /// <summary>The largest request body the route reads, in bytes. A longer body is refused with 413.</summary>
    public int MaxRequestBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// The largest reply the model may be asked for, in tokens. A request that asks for more (<c>max_tokens</c> or
    /// <c>max_completion_tokens</c>) is lowered to this, and a request that asks for nothing is given this: the host never relies on
    /// the service's own default.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// The field that carries the limit when a request names none: <c>max_tokens</c> (the default, which services that speak this wire
    /// widely accept) or <c>max_completion_tokens</c> (what some newer models want instead). A request that names either keeps its own
    /// field, lowered to <see cref="MaxOutputTokens"/> if it asks for more.
    /// </summary>
    public string OutputLimitField { get; set; } = "max_tokens";

    /// <summary>How long a call to the model service may take before the host gives up on it (504).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}
