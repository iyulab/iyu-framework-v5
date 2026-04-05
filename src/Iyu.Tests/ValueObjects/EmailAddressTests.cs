using Iyu.Core.ValueObjects;
using Xunit;

namespace Iyu.Tests.ValueObjects;

public class EmailAddressTests
{
    [Theory]
    [InlineData("user@example.com", "user@example.com")]
    [InlineData("  User@Example.COM  ", "user@example.com")]
    [InlineData("first.last+tag@sub.example.co.kr", "first.last+tag@sub.example.co.kr")]
    public void TryParse_normalizes_to_lowercase_trimmed(string input, string expected)
    {
        Assert.True(EmailAddress.TryParse(input, out var email));
        Assert.Equal(expected, email.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noatsign.com")]
    [InlineData("@nolocal.com")]
    [InlineData("user@")]
    [InlineData("user@localhost")]       // no TLD
    [InlineData(null)]
    public void TryParse_rejects_invalid(string? input)
    {
        Assert.False(EmailAddress.TryParse(input, out _));
    }

    [Fact]
    public void Parse_throws_on_invalid()
    {
        Assert.Throws<FormatException>(() => EmailAddress.Parse("not-an-email"));
    }

    [Fact]
    public void Equality_ignores_original_case()
    {
        var a = EmailAddress.Parse("User@Example.COM");
        var b = EmailAddress.Parse("user@example.com");
        Assert.Equal(a, b);
    }
}
