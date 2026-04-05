using System.Diagnostics.CodeAnalysis;

namespace Iyu.Core.ValueObjects;

/// <summary>
/// Web URL value object. Accepts only absolute http/https URIs; relative paths
/// are rejected to keep this type unambiguously a link to an external resource.
/// </summary>
public readonly record struct WebUrl
{
    /// <summary>The absolute URL as a string (canonicalized via <see cref="Uri"/>).</summary>
    public string Value { get; }

    private WebUrl(string value) => Value = value;

    public static WebUrl Parse(string input)
    {
        if (!TryParse(input, out var result))
            throw new FormatException($"Invalid web URL: '{input}'");
        return result;
    }

    public static bool TryParse([NotNullWhen(true)] string? input, out WebUrl result)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            result = default;
            return false;
        }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            result = default;
            return false;
        }

        result = new WebUrl(uri.ToString());
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
