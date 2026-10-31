using Iyu.Uncad;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Uncad;

public class IyuUncadOptionsTests
{
    private static ValidateOptionsResult Validate(IyuUncadOptions options)
        => new IyuUncadOptionsValidator().Validate(name: null, options);

    [Fact]
    public void The_defaults_are_a_thirty_second_limit_and_a_64_MiB_output_cap_with_no_executable()
    {
        var options = new IyuUncadOptions();

        Assert.Null(options.ExePath);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(64L * 1024 * 1024, options.MaxOutputBytes);
    }

    [Fact]
    public void Options_with_a_path_and_positive_limits_are_valid()
    {
        var result = Validate(new IyuUncadOptions
        {
            ExePath = "uncad",
            Timeout = TimeSpan.FromMilliseconds(1),
            MaxOutputBytes = 1,
        });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_executable_path_is_refused_and_the_setting_is_named(string? exePath)
    {
        var result = Validate(new IyuUncadOptions { ExePath = exePath });

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains(nameof(IyuUncadOptions.ExePath), failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-123)]
    public void A_timeout_that_is_not_positive_is_refused_without_repeating_the_value(int seconds)
    {
        var result = Validate(new IyuUncadOptions { ExePath = "uncad", Timeout = TimeSpan.FromSeconds(seconds) });

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains(nameof(IyuUncadOptions.Timeout), failure);
        Assert.DoesNotContain("123", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void An_infinite_timeout_is_refused_because_no_limit_is_not_a_setting()
    {
        var result = Validate(new IyuUncadOptions { ExePath = "uncad", Timeout = System.Threading.Timeout.InfiniteTimeSpan });

        Assert.True(result.Failed);
        Assert.Contains(nameof(IyuUncadOptions.Timeout), Assert.Single(result.Failures!));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-987654L)]
    public void An_output_cap_that_is_not_positive_is_refused_without_repeating_the_value(long bytes)
    {
        var result = Validate(new IyuUncadOptions { ExePath = "uncad", MaxOutputBytes = bytes });

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains(nameof(IyuUncadOptions.MaxOutputBytes), failure);
        Assert.DoesNotContain("987654", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_wrong_setting_is_reported_at_once()
    {
        var result = Validate(new IyuUncadOptions { ExePath = null, Timeout = TimeSpan.Zero, MaxOutputBytes = 0 });

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures!.Count());
    }
}
