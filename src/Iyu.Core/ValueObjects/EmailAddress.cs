using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Iyu.Core.ValueObjects;

/// <summary>
/// Email address value object. Validation applies a pragmatic regex rather than
/// full RFC 5322 compliance — it covers the 99% case (localpart@domain.tld)
/// without the false-positive risk of a strict grammar. Input is lowercased on
/// parse so that equality semantics match typical identity comparisons.
/// </summary>
public readonly record struct EmailAddress
{
    private static readonly Regex Pattern = new(
        @"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The canonical (lower-cased, trimmed) string form.</summary>
    public string Value { get; }

    private EmailAddress(string value) => Value = value;

    public static EmailAddress Parse(string input)
    {
        if (!TryParse(input, out var result))
            throw new FormatException($"Invalid email address: '{input}'");
        return result;
    }

    public static bool TryParse([NotNullWhen(true)] string? input, out EmailAddress result)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            result = default;
            return false;
        }

        var normalized = input.Trim().ToLowerInvariant();
        if (!Pattern.IsMatch(normalized))
        {
            result = default;
            return false;
        }

        result = new EmailAddress(normalized);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
