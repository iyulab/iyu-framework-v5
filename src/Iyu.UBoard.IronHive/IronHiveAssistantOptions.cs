using Microsoft.Extensions.Options;

namespace Iyu.UBoard.IronHive;

/// <summary>
/// What the host tells the assistant about the model service it uses. The package reads nothing from the environment or from
/// configuration on its own: whatever the host wants taken from there, the host reads and sets here. The key, the model and the limits
/// are the host's alone — a request from a page cannot name or change any of them.
/// </summary>
public sealed class IronHiveAssistantOptions
{
    /// <summary>The service's address: any OpenAI-compatible service (Ollama, LM Studio, vLLM, a gateway in front of a hosted model). Required.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The key the service wants, if it wants one.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The model to ask for. Required.</summary>
    public string? Model { get; set; }

    /// <summary>The most a reply may take, in tokens. The assistant is asked not to think before it answers, so the limit is for the answer.</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>The <c>User-Agent</c> sent to the service, which may refuse a client that names none.</summary>
    public string UserAgent { get; set; } = "Iyu.UBoard.IronHive";

    /// <summary>Further headers sent to the service with every request.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
}

internal sealed class IronHiveAssistantOptionsValidator : IValidateOptions<IronHiveAssistantOptions>
{
    public ValidateOptionsResult Validate(string? name, IronHiveAssistantOptions options)
    {
        var missing = new List<string>();
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            missing.Add(nameof(IronHiveAssistantOptions.BaseUrl) + " (an http or https address)");
        if (string.IsNullOrWhiteSpace(options.Model)) missing.Add(nameof(IronHiveAssistantOptions.Model));
        if (options.MaxOutputTokens < 1) missing.Add(nameof(IronHiveAssistantOptions.MaxOutputTokens) + " (at least 1)");

        // The message names what is missing, never a value: the key and the address are the host's to keep out of its logs.
        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("AddIyuUBoardIronHive needs " + string.Join(", ", missing));
    }
}
