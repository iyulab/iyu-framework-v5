using Iyu.UVision;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.UVision;

public class IyuUVisionOptionsTests
{
    private const string Address = "http://engine.test:8000/api/u-vision";

    private static ValidateOptionsResult Validate(IyuUVisionOptions options)
        => new IyuUVisionOptionsValidator().Validate(name: null, options);

    [Fact]
    public void The_defaults_are_a_thirty_second_limit_and_a_12_MiB_image_cap_with_no_address()
    {
        var options = new IyuUVisionOptions();

        Assert.Null(options.BaseUrl);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(12L * 1024 * 1024, options.MaxImageBytes);
    }

    [Fact]
    public void Options_with_an_address_and_usable_limits_are_valid()
    {
        var result = Validate(new IyuUVisionOptions
        {
            BaseUrl = Address,
            Timeout = TimeSpan.FromMilliseconds(1),
            MaxImageBytes = 1,
        });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("http://localhost:8000")]
    [InlineData("https://engine.test/api/u-vision/")]
    [InlineData("  https://engine.test/api/u-vision  ")]
    public void An_absolute_http_or_https_address_is_accepted(string baseUrl)
    {
        Assert.True(Validate(new IyuUVisionOptions { BaseUrl = baseUrl }).Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_address_is_refused_and_the_setting_is_named(string? baseUrl)
    {
        var result = Validate(new IyuUVisionOptions { BaseUrl = baseUrl });

        Assert.True(result.Failed);
        Assert.Contains(nameof(IyuUVisionOptions.BaseUrl), Assert.Single(result.Failures!));
    }

    // "/api/u-vision" parses as an absolute file:// URI on Linux and "localhost:8000" as the scheme "localhost": only the scheme
    // check tells a configuration mistake from an address.
    [Theory]
    [InlineData("/api/u-vision")]
    [InlineData("localhost:8000")]
    [InlineData("engine.test/api/u-vision")]
    [InlineData("ftp://engine.test/api/u-vision")]
    [InlineData("file:///api/u-vision")]
    [InlineData("api/u-vision")]
    public void An_address_that_is_not_an_absolute_http_or_https_URL_is_refused_without_repeating_it(string baseUrl)
    {
        var result = Validate(new IyuUVisionOptions { BaseUrl = baseUrl });

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains(nameof(IyuUVisionOptions.BaseUrl), failure);
        Assert.DoesNotContain(baseUrl, failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://engine.test/api/u-vision?key=1")]
    [InlineData("http://engine.test/api/u-vision#top")]
    public void A_query_or_fragment_is_refused_because_it_would_be_dropped(string baseUrl)
    {
        var result = Validate(new IyuUVisionOptions { BaseUrl = baseUrl });

        Assert.True(result.Failed);
        Assert.Contains(nameof(IyuUVisionOptions.BaseUrl), Assert.Single(result.Failures!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-123)]
    public void A_timeout_that_is_not_positive_is_refused_without_repeating_the_value(int seconds)
    {
        var result = Validate(new IyuUVisionOptions { BaseUrl = Address, Timeout = TimeSpan.FromSeconds(seconds) });

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains(nameof(IyuUVisionOptions.Timeout), failure);
        Assert.DoesNotContain("123", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void An_infinite_timeout_is_refused_because_no_limit_is_not_a_setting()
    {
        var result = Validate(new IyuUVisionOptions { BaseUrl = Address, Timeout = System.Threading.Timeout.InfiniteTimeSpan });

        Assert.True(result.Failed);
        Assert.Contains(nameof(IyuUVisionOptions.Timeout), Assert.Single(result.Failures!));
    }

    [Fact]
    public void A_timeout_longer_than_the_HTTP_client_accepts_is_refused_at_startup_not_at_the_first_call()
    {
        var result = Validate(new IyuUVisionOptions { BaseUrl = Address, Timeout = TimeSpan.FromDays(30) });

        Assert.True(result.Failed);
        Assert.Contains(nameof(IyuUVisionOptions.Timeout), Assert.Single(result.Failures!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpClient { Timeout = TimeSpan.FromDays(30) });
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-987654L)]
    public void An_image_cap_that_is_not_positive_is_refused_without_repeating_the_value(long bytes)
    {
        var result = Validate(new IyuUVisionOptions { BaseUrl = Address, MaxImageBytes = bytes });

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains(nameof(IyuUVisionOptions.MaxImageBytes), failure);
        Assert.DoesNotContain("987654", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_wrong_setting_is_reported_at_once()
    {
        var result = Validate(new IyuUVisionOptions { BaseUrl = null, Timeout = TimeSpan.Zero, MaxImageBytes = 0 });

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures!.Count());
    }

    [Theory]
    [InlineData("http://engine.test:8000/api/u-vision")]
    [InlineData("http://engine.test:8000/api/u-vision/")]
    [InlineData("http://engine.test:8000/api/u-vision///")]
    [InlineData("  http://engine.test:8000/api/u-vision/  ")]
    public void The_base_address_has_exactly_one_trailing_slash_and_keeps_the_base_path(string baseUrl)
    {
        var address = new IyuUVisionOptions { BaseUrl = baseUrl }.BaseAddress();

        Assert.Equal("http://engine.test:8000/api/u-vision/", address.ToString());
        Assert.Equal("http://engine.test:8000/api/u-vision/inspect", new Uri(address, "inspect").ToString());
    }

    [Fact]
    public void An_address_with_no_path_resolves_to_the_host_root()
    {
        var address = new IyuUVisionOptions { BaseUrl = "http://engine.test:8000" }.BaseAddress();

        Assert.Equal("http://engine.test:8000/inspect", new Uri(address, "inspect").ToString());
    }
}
