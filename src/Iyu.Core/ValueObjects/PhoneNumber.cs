using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Iyu.Core.ValueObjects;

/// <summary>
/// Phone number value object. Accepts international and domestic formats; the
/// canonical stored form is the input trimmed of surrounding whitespace. Validation
/// is intentionally lenient — it rejects obviously non-numeric junk while leaving
/// format normalization (E.164 etc.) to a higher layer if needed.
/// </summary>
public readonly record struct PhoneNumber
{
    // Permits digits, whitespace, parentheses, hyphens, dots, and a leading '+'.
    // Requires at least 6 digits overall to reject near-empty strings.
    private static readonly Regex Pattern = new(
        @"^\+?[\d\s().\-]{6,30}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The canonical string representation (trimmed input).</summary>
    public string Value { get; }

    private PhoneNumber(string value) => Value = value;

    /// <summary>
    /// Parses the input into a <see cref="PhoneNumber"/>. Throws
    /// <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static PhoneNumber Parse(string input)
    {
        if (!TryParse(input, out var result))
            throw new FormatException($"Invalid phone number: '{input}'");
        return result;
    }

    /// <summary>
    /// Attempts to parse the input. Returns true on success; otherwise the
    /// <paramref name="result"/> is <c>default</c>.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? input, out PhoneNumber result)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            result = default;
            return false;
        }

        var trimmed = input.Trim();
        if (!Pattern.IsMatch(trimmed) || trimmed.Count(char.IsDigit) < 6)
        {
            result = default;
            return false;
        }

        result = new PhoneNumber(trimmed);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
