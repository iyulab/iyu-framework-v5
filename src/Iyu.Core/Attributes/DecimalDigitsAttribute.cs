using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Iyu.Core.Attributes;

/// <summary>
/// A <c>decimal(precision, scale)</c> value: at most <c>precision − scale</c> digits before the decimal point and
/// <c>scale</c> after it. Emitted by mdd-booster on M3L <c>decimal(p,s)</c> fields, beside the column type — the way
/// <see cref="StringLengthAttribute"/> stands beside a string column.
/// </summary>
/// <remarks>
/// <para>
/// A value beyond the column is refused rather than stored: the database would otherwise round the extra decimal
/// places away (the caller reads back a number it never sent) or fail the write on the integer part. Refused here, it
/// is a model-validation failure the data API answers like any other.
/// </para>
/// <para>
/// Trailing zeros are scale, not digits — <c>1.2000</c> fits two decimal places. <c>null</c> is valid: whether a value
/// is required is <see cref="RequiredAttribute"/>'s to say.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class DecimalDigitsAttribute : ValidationAttribute
{
    /// <param name="precision">The total number of digits the column keeps.</param>
    /// <param name="scale">How many of them come after the decimal point.</param>
    public DecimalDigitsAttribute(int precision, int scale)
        : base("The field {0} allows at most {1} digits before the decimal point and {2} after it.")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, precision);
        Precision = precision;
        Scale = scale;
    }

    /// <summary>The total number of digits the column keeps.</summary>
    public int Precision { get; }

    /// <summary>How many digits come after the decimal point.</summary>
    public int Scale { get; }

    public override bool IsValid(object? value)
    {
        if (value is null) return true;
        decimal number;
        try { number = Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException) { return false; }

        // Dividing by one with the largest scale strips trailing zeros: 1.2000m becomes 1.2m.
        var normalized = number / 1.0000000000000000000000000000m;
        var scale = (decimal.GetBits(normalized)[3] >> 16) & 0xFF;
        var whole = decimal.Truncate(Math.Abs(normalized));
        var integerDigits = whole == 0 ? 0 : whole.ToString(CultureInfo.InvariantCulture).Length;
        return scale <= Scale && integerDigits <= Precision - Scale;
    }

    public override string FormatErrorMessage(string name)
        => string.Format(CultureInfo.CurrentCulture, ErrorMessageString, name, Precision - Scale, Scale);
}
