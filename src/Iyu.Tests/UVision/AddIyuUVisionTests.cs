using System.Net;
using System.Text;
using Iyu.Tests.DocConvert;
using Iyu.UVision;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.UVision;

public sealed class AddIyuUVisionTests
{
    private const string Address = "http://engine.test/api/u-vision";

    private static WebApplicationBuilder Host(string environment = "Production")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        return builder;
    }

    private static HttpResponseMessage Answer(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void Nothing_is_registered_until_AddIyuUVision_is_called()
    {
        var builder = Host();
        using var app = builder.Build();

        Assert.Null(app.Services.GetService<IVisionEngine>());
        Assert.Null(app.Services.GetService<IHttpClientFactory>());
        Assert.Null(app.Services.GetService<IValidateOptions<IyuUVisionOptions>>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/api/u-vision")]
    [InlineData("localhost:8000")]
    public async Task A_host_that_turned_it_on_without_a_usable_address_does_not_start(string? baseUrl)
    {
        var builder = Host();
        builder.Services.AddIyuUVision(o => o.BaseUrl = baseUrl);
        await using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        Assert.Contains(nameof(IyuUVisionOptions.BaseUrl), Assert.Single(refusal.Failures));
    }

    [Fact]
    public async Task A_host_with_a_limit_that_is_not_usable_does_not_start()
    {
        var builder = Host();
        builder.Services.AddIyuUVision(o =>
        {
            o.BaseUrl = Address;
            o.MaxImageBytes = 0;
        });
        await using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        Assert.Contains(nameof(IyuUVisionOptions.MaxImageBytes), Assert.Single(refusal.Failures));
    }

    [Fact]
    public async Task A_configured_host_starts_and_serves_the_engine_and_the_options()
    {
        var builder = Host();
        builder.Services.AddIyuUVision(o =>
        {
            o.BaseUrl = Address;
            o.Timeout = TimeSpan.FromSeconds(7);
            o.MaxImageBytes = 2048;
        });
        await using var app = builder.Build();
        await app.StartAsync();

        Assert.IsType<UVisionEngine>(app.Services.GetRequiredService<IVisionEngine>());
        var options = app.Services.GetRequiredService<IOptions<IyuUVisionOptions>>().Value;
        Assert.Equal((Address, TimeSpan.FromSeconds(7), 2048L), (options.BaseUrl, options.Timeout, options.MaxImageBytes));
    }

    [Fact]
    public async Task The_address_and_the_time_limit_reach_the_HTTP_client()
    {
        var builder = Host();
        builder.Services.AddIyuUVision(o =>
        {
            o.BaseUrl = Address;                 // no trailing slash, the natural way to type it
            o.Timeout = TimeSpan.FromSeconds(7);
        });
        await using var app = builder.Build();
        await app.StartAsync();

        var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IVisionEngine));

        Assert.Equal("http://engine.test/api/u-vision/", client.BaseAddress!.ToString());
        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }

    [Fact]
    public async Task A_call_through_the_container_reaches_the_engine_at_its_base_path_and_reads_the_verdict()
    {
        Uri? seen = null;
        var builder = Host();
        builder.Services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler((request, _) =>
        {
            seen = request.RequestUri;
            return Task.FromResult(Answer("""{"verdict": "NG", "image_id": "img-3", "confidence": 0.6}"""));
        })));
        builder.Services.AddIyuUVision(o => o.BaseUrl = Address);
        await using var app = builder.Build();
        await app.StartAsync();

        var result = await app.Services.GetRequiredService<IVisionEngine>()
            .InspectAsync("scenario-1", new byte[] { 1, 2 }, "a.jpg", null);

        Assert.Equal("http://engine.test/api/u-vision/inspect", seen!.ToString());
        Assert.Equal(new VisionVerdict("NG", 0.6m, "img-3", false, null), result.Verdict);
    }

    [Fact]
    public async Task A_call_through_the_container_ends_unavailable_when_the_configured_time_limit_passes()
    {
        var builder = Host();
        builder.Services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        })));
        builder.Services.AddIyuUVision(o =>
        {
            o.BaseUrl = Address;
            o.Timeout = TimeSpan.FromMilliseconds(150);
        });
        await using var app = builder.Build();
        await app.StartAsync();

        var result = await app.Services.GetRequiredService<IVisionEngine>()
            .InspectAsync("scenario-1", new byte[] { 1 }, "a.jpg", null);

        Assert.Equal(VisionEngineFailure.Unavailable, result.Failure);
    }

    [Fact]
    public async Task A_call_through_the_container_applies_the_configured_image_cap()
    {
        var calls = 0;
        var builder = Host();
        builder.Services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new FakeHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Answer("""{"verdict": "OK", "image_id": "i"}"""));
        })));
        builder.Services.AddIyuUVision(o =>
        {
            o.BaseUrl = Address;
            o.MaxImageBytes = 3;
        });
        await using var app = builder.Build();
        await app.StartAsync();

        var result = await app.Services.GetRequiredService<IVisionEngine>()
            .InspectAsync("scenario-1", new byte[4], "a.jpg", null);

        Assert.Equal(VisionEngineFailure.Rejected, result.Failure);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_host_can_replace_the_engine_after_the_call()
    {
        var builder = Host();
        builder.Services.AddIyuUVision(o => o.BaseUrl = Address);
        builder.Services.AddSingleton<IVisionEngine, FixedEngine>();
        await using var app = builder.Build();
        await app.StartAsync();

        Assert.IsType<FixedEngine>(app.Services.GetRequiredService<IVisionEngine>());
    }

    [Fact]
    public void The_whole_container_validates_in_Development()
    {
        var builder = Host("Development");
        builder.Services.AddIyuUVision(o => o.BaseUrl = Address);

        using var app = builder.Build();
    }

    [Fact]
    public void Missing_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => IyuUVisionServiceCollectionExtensions.AddIyuUVision(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddIyuUVision(null!));
    }

    private sealed class FixedEngine : IVisionEngine
    {
        public Task<VisionEngineResult> InspectAsync(
            string scenarioId, ReadOnlyMemory<byte> image, string fileName, string? deviceLabel, CancellationToken cancellationToken = default)
            => Task.FromResult(VisionEngineResult.Fail(VisionEngineFailure.Unavailable, "fixed"));
    }
}
