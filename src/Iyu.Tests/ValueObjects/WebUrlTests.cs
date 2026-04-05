using Iyu.Core.ValueObjects;
using Xunit;

namespace Iyu.Tests.ValueObjects;

public class WebUrlTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?query=1")]
    [InlineData("https://sub.example.co.kr/deep/path")]
    public void TryParse_accepts_http_and_https(string input)
    {
        Assert.True(WebUrl.TryParse(input, out var url));
        Assert.StartsWith(input.Split('?')[0].TrimEnd('/'), url.Value.TrimEnd('/'));
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///c:/x")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]          // missing scheme
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_rejects_non_http(string? input)
    {
        Assert.False(WebUrl.TryParse(input, out _));
    }

    [Fact]
    public void Parse_throws_on_invalid()
    {
        Assert.Throws<FormatException>(() => WebUrl.Parse("not-a-url"));
    }
}
