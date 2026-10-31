using Iyu.Uncad;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Uncad;

public sealed class AddIyuUncadTests : IDisposable
{
    private readonly FakeUncadDirectory _fake = new();

    public void Dispose() => _fake.Dispose();

    private static WebApplicationBuilder Host(string environment = "Production")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        return builder;
    }

    [Fact]
    public void Nothing_is_registered_until_AddIyuUncad_is_called()
    {
        var builder = Host();
        using var app = builder.Build();

        Assert.Null(app.Services.GetService<IUncadRunner>());
        Assert.Null(app.Services.GetService<DrawingProbeService>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_host_that_turned_it_on_without_an_executable_path_does_not_start(string? exePath)
    {
        var builder = Host();
        builder.Services.AddIyuUncad(o => o.ExePath = exePath);
        await using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        var message = Assert.Single(refusal.Failures);
        Assert.Contains("ExePath", message);
    }

    [Fact]
    public async Task A_host_with_a_limit_that_is_not_positive_does_not_start()
    {
        var builder = Host();
        builder.Services.AddIyuUncad(o =>
        {
            o.ExePath = _fake.ExecutablePath("uncad");
            o.Timeout = TimeSpan.Zero;
        });
        await using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        Assert.Contains("Timeout", Assert.Single(refusal.Failures));
    }

    [Fact]
    public async Task A_configured_host_starts_and_serves_the_runner_the_service_and_the_options()
    {
        var path = _fake.ExecutablePath("uncad");
        var builder = Host();
        builder.Services.AddIyuUncad(o =>
        {
            o.ExePath = path;
            o.Timeout = TimeSpan.FromSeconds(7);
            o.MaxOutputBytes = 2048;
        });
        await using var app = builder.Build();
        await app.StartAsync();

        Assert.IsType<UncadRunner>(app.Services.GetRequiredService<IUncadRunner>());
        Assert.Same(app.Services.GetRequiredService<IUncadRunner>(), app.Services.GetRequiredService<IUncadRunner>());
        Assert.Same(app.Services.GetRequiredService<DrawingProbeService>(), app.Services.GetRequiredService<DrawingProbeService>());
        var options = app.Services.GetRequiredService<IOptions<IyuUncadOptions>>().Value;
        Assert.Equal((path, TimeSpan.FromSeconds(7), 2048L), (options.ExePath, options.Timeout, options.MaxOutputBytes));
    }

    [Fact]
    public async Task A_path_with_no_file_behind_it_does_not_stop_the_host_and_a_later_install_is_used()
    {
        // The program can be installed after the host has started, so the file is looked for when a drawing is read, not at startup.
        var path = _fake.ExecutablePath("uncad");
        var drawing = _fake.Drawing();
        var builder = Host();
        builder.Services.AddIyuUncad(o => o.ExePath = path);
        await using var app = builder.Build();
        await app.StartAsync();
        var probe = app.Services.GetRequiredService<DrawingProbeService>();

        var before = await probe.ProbeAsync(drawing);

        Assert.False(before.Ok);
        Assert.Equal(UncadFailure.NotConfigured, before.Failure);

        _fake.Executable(
            "uncad",
            windows: ["echo {\"entity_count\":4,\"confidence\":\"HIGH\"}"],
            unix: ["echo '{\"entity_count\":4,\"confidence\":\"HIGH\"}'"]);
        var after = await probe.ProbeAsync(drawing);

        Assert.True(after.Ok, after.Message);
        Assert.Equal(4, after.Probe!.EntityCount);
        Assert.Equal("HIGH", after.Probe.Confidence);
    }

    [Fact]
    public void The_whole_container_validates_in_Development()
    {
        var builder = Host("Development");
        builder.Services.AddIyuUncad(o => o.ExePath = _fake.ExecutablePath("uncad"));

        using var app = builder.Build();
    }

    [Fact]
    public void Missing_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => IyuUncadServiceCollectionExtensions.AddIyuUncad(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddIyuUncad(null!));
    }
}
