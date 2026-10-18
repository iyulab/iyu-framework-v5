using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Iyu.Core.Attributes;

/// <summary>
/// A rule each element of a list must keep — the per-value rule a column of single values keeps, applied to every
/// element of a list property. The validation attributes of <c>System.ComponentModel.DataAnnotations</c> judge the
/// property's value as a whole, so on a list <see cref="StringLengthAttribute"/> or <c>LengthAttribute</c> count the
/// elements rather than measure them.
/// </summary>
/// <remarks>
/// <para>
/// A <c>null</c> list is valid, and so is a <c>null</c> element: whether either may be absent is the declaration's to
/// say (<see cref="RequiredAttribute"/>, the element type's nullability), not the rule's. A value that is not a list —
/// or is a <see cref="string"/>, which enumerates as characters — is refused: the rule does not apply to it, and passing
/// it would hide a declaration on the wrong property.
/// </para>
/// <para>
/// The message names the first element that breaks the rule by its position, so a caller sending a list of many
/// values learns which one to fix.
/// </para>
/// </remarks>
public abstract class ElementValidationAttribute : ValidationAttribute
{
    private protected ElementValidationAttribute(string errorMessage) : base(errorMessage) { }

    /// <summary>Whether one non-null element keeps the rule.</summary>
    protected abstract bool IsValidElement(object element);

    /// <summary>The message for the element at <paramref name="index"/> of the list named <paramref name="name"/>.</summary>
    protected abstract string FormatElementErrorMessage(string name, int index);

    public override bool IsValid(object? value) => FirstInvalid(value) is null;

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        ArgumentNullException.ThrowIfNull(validationContext);
        var invalid = FirstInvalid(value);
        if (invalid is null) return ValidationResult.Success;
        var name = validationContext.DisplayName;
        var message = invalid == NotAList
            ? string.Format(CultureInfo.CurrentCulture, "The field {0} is not a list of values.", name)
            : FormatElementErrorMessage(name, invalid.Value);
        return new ValidationResult(message, validationContext.MemberName is { } member ? [member] : null);
    }

    private const int NotAList = -1;

    /// <summary>The index of the first element that breaks the rule, <see cref="NotAList"/>, or null when none does.</summary>
    private int? FirstInvalid(object? value)
    {
        if (value is null) return null;
        if (value is string || value is not IEnumerable elements) return NotAList;
        var index = 0;
        foreach (var element in elements)
        {
            if (element is not null && !IsValidElement(element)) return index;
            index++;
        }
        return null;
    }
}
