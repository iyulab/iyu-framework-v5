using Iyu.Core.ValueObjects;
using Xunit;

namespace Iyu.Tests.ValueObjects;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("010-1234-5678")]
    [InlineData("+82 10 1234 5678")]
    [InlineData("(02) 555-1234")]
    [InlineData("+1.415.555.2671")]
    public void TryParse_accepts_realistic_inputs(string input)
    {
        Assert.True(PhoneNumber.TryParse(input, out var phone));
        Assert.Equal(input.Trim(), phone.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcd")]
    [InlineData("123")]                 // < 6 digits
    [InlineData("+82-x-y-z")]
    [InlineData(null)]
    public void TryParse_rejects_garbage(string? input)
    {
        Assert.False(PhoneNumber.TryParse(input, out _));
    }

    [Fact]
    public void Parse_throws_on_invalid()
    {
        Assert.Throws<FormatException>(() => PhoneNumber.Parse("nope"));
    }

    [Fact]
    public void Equality_is_value_based()
    {
        var a = PhoneNumber.Parse("010-1234-5678");
        var b = PhoneNumber.Parse("010-1234-5678");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }
}
