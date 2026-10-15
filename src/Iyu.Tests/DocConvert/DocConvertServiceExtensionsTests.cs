using Iyu.DocConvert;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.DocConvert;

public sealed class DocConvertServiceExtensionsTests
{
    [Fact]
    public void AddIyuDocConvert_resolves_IDocumentConverter_as_the_Gotenberg_implementation()
    {
        var services = new ServiceCollection();
        services.AddIyuDocConvert();

        using var provider = services.BuildServiceProvider();
        var converter = provider.GetRequiredService<IDocumentConverter>();

        Assert.IsType<GotenbergDocumentConverter>(converter);
    }

    [Fact]
    public void Defaults_to_localhost_3000_when_not_configured()
    {
        var services = new ServiceCollection();
        services.AddIyuDocConvert();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(nameof(IDocumentConverter));

        Assert.Equal("http://localhost:3000/", client.BaseAddress!.ToString());
    }

    [Fact]
    public void Configure_callback_sets_the_typed_clients_base_address_and_timeout()
    {
        var services = new ServiceCollection();
        services.AddIyuDocConvert(o =>
        {
            o.BaseUrl = "http://gotenberg.internal:3000";
            o.Timeout = TimeSpan.FromSeconds(45);
        });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(nameof(IDocumentConverter));

        // No trailing slash in the input — the extension must normalize it, or HttpClient
        // silently drops the request to the wrong (host-relative) URI at request time.
        Assert.Equal("http://gotenberg.internal:3000/", client.BaseAddress!.ToString());
        Assert.Equal(TimeSpan.FromSeconds(45), client.Timeout);
    }

    // A base URL without a scheme parses: "localhost:3000" reads as the scheme "localhost". The
    // mistake then surfaced on the first conversion as the NotSupportedException the contract
    // reserves for an unhandled content type — a configuration error reported as a document one.
    [Theory]
    [InlineData("localhost:3000")]
    [InlineData("gotenberg:3000")]
    [InlineData("/gotenberg")]
    [InlineData("ftp://gotenberg.internal")]
    [InlineData("")]
    public void A_base_url_that_is_not_an_absolute_http_url_is_refused_at_registration(string baseUrl)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => services.AddIyuDocConvert(o => o.BaseUrl = baseUrl));

        Assert.Contains("GotenbergOptions.BaseUrl", ex.Message);
    }

    [Theory]
    [InlineData("http://gotenberg.internal:3000")]
    [InlineData("https://gotenberg.example/convert/")]
    public void An_absolute_http_or_https_base_url_is_accepted(string baseUrl)
    {
        var services = new ServiceCollection();

        services.AddIyuDocConvert(o => o.BaseUrl = baseUrl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_timeout_that_is_not_positive_is_refused_at_registration(int seconds)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(
            () => services.AddIyuDocConvert(o => o.Timeout = TimeSpan.FromSeconds(seconds)));

        Assert.Contains("GotenbergOptions.Timeout", ex.Message);
    }

    [Fact]
    public void An_infinite_timeout_is_accepted()
    {
        var services = new ServiceCollection();

        services.AddIyuDocConvert(o => o.Timeout = Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void GotenbergOptions_is_registered_for_the_host_to_read_back()
    {
        var services = new ServiceCollection();
        services.AddIyuDocConvert(o => o.BaseUrl = "http://gotenberg.internal:3000");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<GotenbergOptions>();

        Assert.Equal("http://gotenberg.internal:3000", options.BaseUrl);
    }
}
