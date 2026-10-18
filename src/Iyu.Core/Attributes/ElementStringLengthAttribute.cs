using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Iyu.Core.Attributes;

/// <summary>
/// Each element of a list of strings is at most <see cref="MaximumLength"/> characters — the <c>string(n)</c> of a
/// <c>string(n)[]</c> field. Emitted by mdd-booster on M3L list fields with a length, where
/// <see cref="StringLengthAttribute"/> stands on a single-value field.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class ElementStringLengthAttribute : ElementValidationAttribute
{
    public ElementStringLengthAttribute(int maximumLength)
        : base("The field {0} allows elements of at most {1} characters; element {2} is longer.")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        MaximumLength = maximumLength;
    }

    /// <summary>The most characters one element may have.</summary>
    public int MaximumLength { get; }

    protected override bool IsValidElement(object element)
        => element is string s && s.Length <= MaximumLength;

    protected override string FormatElementErrorMessage(string name, int index)
        => string.Format(CultureInfo.CurrentCulture, ErrorMessageString, name, MaximumLength, index);

    public override string FormatErrorMessage(string name)
        => string.Format(CultureInfo.CurrentCulture, "The field {0} allows elements of at most {1} characters.", name, MaximumLength);
}
