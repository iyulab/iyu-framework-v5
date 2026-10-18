using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Iyu.Core.Attributes;

/// <summary>
/// Each element of a list of numbers fits <c>decimal(precision, scale)</c> — the rule of
/// <see cref="DecimalDigitsAttribute"/>, applied to every element of a <c>decimal(p,s)[]</c> field. Emitted by
/// mdd-booster on M3L list fields with a precision.
/// </summary>
/// <remarks>
/// A list column keeps its elements' scale no better than a single column does: PostgreSQL's <c>numeric(p,s)[]</c>
/// rounds the extra places away, and SQL Server's JSON text keeps no type at all — this rule is where the value is
/// refused instead.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class ElementDecimalDigitsAttribute : ElementValidationAttribute
{
    private readonly DecimalDigitsAttribute _element;

    /// <param name="precision">The total number of digits an element keeps.</param>
    /// <param name="scale">How many of them come after the decimal point.</param>
    public ElementDecimalDigitsAttribute(int precision, int scale)
        : base("The field {0} allows elements with at most {1} digits before the decimal point and {2} after it; element {3} has more.")
    {
        _element = new DecimalDigitsAttribute(precision, scale);
    }

    /// <summary>The total number of digits an element keeps.</summary>
    public int Precision => _element.Precision;

    /// <summary>How many digits come after the decimal point.</summary>
    public int Scale => _element.Scale;

    protected override bool IsValidElement(object element) => _element.IsValid(element);

    protected override string FormatElementErrorMessage(string name, int index)
        => string.Format(CultureInfo.CurrentCulture, ErrorMessageString, name, Precision - Scale, Scale, index);

    public override string FormatErrorMessage(string name)
        => string.Format(CultureInfo.CurrentCulture,
            "The field {0} allows elements with at most {1} digits before the decimal point and {2} after it.", name, Precision - Scale, Scale);
}
