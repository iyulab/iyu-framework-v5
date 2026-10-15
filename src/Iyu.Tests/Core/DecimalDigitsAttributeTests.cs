using System.ComponentModel.DataAnnotations;
using Iyu.Core.Attributes;
using Xunit;

namespace Iyu.Tests.Core;

/// <summary>
/// A <c>decimal(p,s)</c> column takes at most <c>p − s</c> digits before the point and <c>s</c> after. A value beyond
/// it is refused — not rounded by the database to a different number the caller never sees. Trailing zeros are not
/// digits: <c>1.2000</c> fits two decimal places.
/// </summary>
public class DecimalDigitsAttributeTests
{
    private static bool Fits(object? value, int precision, int scale)
        => new DecimalDigitsAttribute(precision, scale).IsValid(value);

    [Theory]
    [InlineData("49999.1234", 18, 4)]
    [InlineData("49999.12340000", 18, 4)]   // trailing zeros are scale, not digits
    [InlineData("-99.99", 4, 2)]
    [InlineData("0.5", 1, 1)]
    [InlineData("0", 1, 0)]
    [InlineData("12", 2, 0)]
    public void A_value_within_the_declared_digits_is_valid(string value, int precision, int scale)
        => Assert.True(Fits(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), precision, scale));

    [Theory]
    [InlineData("49999.12345", 18, 4)]   // one decimal place too many — the database would round it
    [InlineData("100.00", 4, 2)]         // three digits before the point where two fit
    [InlineData("-100", 4, 2)]
    [InlineData("1.5", 2, 0)]
    public void A_value_beyond_the_declared_digits_is_refused(string value, int precision, int scale)
        => Assert.False(Fits(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), precision, scale));

    [Fact]
    public void Null_is_for_Required_to_judge()
        => Assert.True(Fits(null, 4, 2));

    [Fact]
    public void The_message_names_the_field_and_both_limits()
    {
        var context = new ValidationContext(new object()) { DisplayName = "Amount", MemberName = "Amount" };
        var result = new DecimalDigitsAttribute(6, 2).GetValidationResult(1.234m, context);
        Assert.NotNull(result);
        Assert.Contains("Amount", result!.ErrorMessage);
        Assert.Contains("4", result.ErrorMessage);
        Assert.Contains("2", result.ErrorMessage);
    }

    [Fact]
    public void A_scale_beyond_the_precision_is_a_declaration_error()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DecimalDigitsAttribute(2, 3));
}
