using System.ComponentModel.DataAnnotations;
using Iyu.Core.Attributes;
using Xunit;

namespace Iyu.Tests.Core;

/// <summary>
/// A list property's per-element rule: every element keeps the length or the digits a single value of the same
/// declaration would — the list as a whole is not measured. A null list and a null element are for the declaration to
/// judge; a value that is not a list is refused.
/// </summary>
public class ElementValidationAttributeTests
{
    private static ValidationResult? Validate(ValidationAttribute attribute, object? value)
        => attribute.GetValidationResult(value, new ValidationContext(new object()) { DisplayName = "Tags", MemberName = "Tags" });

    [Fact]
    public void Elements_within_the_length_are_valid_however_many_there_are()
        => Assert.Null(Validate(new ElementStringLengthAttribute(3), new List<string> { "a", "bb", "ccc", "ddd", "eee" }));

    [Fact]
    public void An_element_beyond_the_length_is_refused_by_its_position()
    {
        var result = Validate(new ElementStringLengthAttribute(3), new List<string> { "a", "bbb", "cccc" });

        Assert.NotNull(result);
        Assert.Contains("Tags", result!.ErrorMessage);
        Assert.Contains("3 characters", result.ErrorMessage);
        Assert.Contains("element 2", result.ErrorMessage);
        Assert.Equal(["Tags"], result.MemberNames);
    }

    [Fact]
    public void The_length_is_of_each_element_not_of_the_list()
        => Assert.Null(Validate(new ElementStringLengthAttribute(1), new[] { "a", "b", "c" }));

    [Fact]
    public void Elements_within_the_digits_are_valid_and_trailing_zeros_are_not_digits()
        => Assert.Null(Validate(new ElementDecimalDigitsAttribute(6, 2), new List<decimal> { 1.2m, 9999.99m, 1.2000m }));

    [Fact]
    public void An_element_beyond_the_digits_is_refused_by_its_position()
    {
        var result = Validate(new ElementDecimalDigitsAttribute(6, 2), new List<decimal?> { 1.2m, null, 1.234m });

        Assert.NotNull(result);
        Assert.Contains("element 2", result!.ErrorMessage);
        Assert.Contains("4 digits before the decimal point and 2 after", result.ErrorMessage);
    }

    [Fact]
    public void A_null_list_and_a_null_element_are_for_the_declaration_to_judge()
    {
        Assert.Null(Validate(new ElementStringLengthAttribute(3), null));
        Assert.Null(Validate(new ElementStringLengthAttribute(3), new List<string?> { "a", null }));
        Assert.Null(Validate(new ElementDecimalDigitsAttribute(4, 2), new List<decimal?> { null }));
    }

    /// <summary>A string enumerates as characters — on a string property the rule would measure nothing it means to.</summary>
    [Fact]
    public void A_value_that_is_not_a_list_is_refused()
    {
        var result = Validate(new ElementStringLengthAttribute(3), "abcd");

        Assert.NotNull(result);
        Assert.Contains("is not a list", result!.ErrorMessage);
        Assert.False(new ElementDecimalDigitsAttribute(4, 2).IsValid(1.5m));
    }

    [Fact]
    public void A_declaration_that_cannot_hold_is_refused_when_made()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElementStringLengthAttribute(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElementDecimalDigitsAttribute(2, 3));
    }
}
