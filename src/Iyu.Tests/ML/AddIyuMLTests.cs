using System.Security.Claims;
using Iyu.ML;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Iyu.Tests.ML;

/// <summary>
/// The layer is explicit and fails closed: a host that turns it on without what a dataset needs does not start, and the
/// message says which piece is missing.
/// </summary>
public sealed class AddIyuMLTests
{
    private static readonly MLDatasetDefinition Readings = new("readings", [new("value", MLColumnRole.Feature), new("kind", MLColumnRole.Label)]);

    private sealed class FixedSource(params MLDatasetDefinition[] served) : IMLDatasetSource
    {
        public IReadOnlyList<MLDatasetDefinition> Datasets { get; } = served;

        public IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadAsync(
            string dataset, ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
            MLTestData.AsAsync([]);
    }

    private sealed class FixedGuard : IMLReadGuard
    {
        public string? RefusalFor(Type entity, string property) => null;

        public ValueTask<bool> MayReadAsync(Type entity, ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    private static HostApplicationBuilder Host() =>
        Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });

    [Fact]
    public async Task A_host_with_a_dataset_a_source_serving_it_and_no_read_policies_starts()
    {
        var builder = Host();
        builder.Services.AddSingleton<IMLDatasetSource>(new FixedSource(Readings));
        builder.Services.AddIyuML(o => o.Dataset(Readings).NoReadPolicies());
        using var app = builder.Build();

        await app.StartAsync();
        await app.StopAsync();
    }

    [Fact]
    public async Task A_host_with_a_guard_starts_without_saying_it_has_no_read_policies()
    {
        var builder = Host();
        builder.Services.AddSingleton<IMLDatasetSource>(new FixedSource(Readings));
        builder.Services.AddSingleton<IMLReadGuard, FixedGuard>();
        builder.Services.AddIyuML(o => o.Dataset(Readings));
        using var app = builder.Build();

        await app.StartAsync();
        await app.StopAsync();
    }

    [Fact]
    public async Task The_source_may_be_registered_after_the_layer_is_turned_on()
    {
        var builder = Host();
        builder.Services.AddIyuML(o => o.Dataset(Readings).NoReadPolicies());
        builder.Services.AddSingleton<IMLDatasetSource>(new FixedSource(Readings));
        using var app = builder.Build();

        await app.StartAsync();
        await app.StopAsync();
    }

    [Fact]
    public async Task A_host_that_declares_no_dataset_does_not_start()
    {
        var builder = Host();
        builder.Services.AddSingleton<IMLDatasetSource>(new FixedSource());
        builder.Services.AddIyuML(o => o.NoReadPolicies());
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());

        Assert.Contains("at least one dataset", refusal.Message);
    }

    [Fact]
    public async Task A_host_with_no_source_does_not_start_and_the_message_names_the_port()
    {
        var builder = Host();
        builder.Services.AddIyuML(o => o.Dataset(Readings).NoReadPolicies());
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("IMLDatasetSource", refusal.Message);
    }

    [Fact]
    public async Task A_declared_dataset_the_source_does_not_serve_is_named()
    {
        var builder = Host();
        builder.Services.AddSingleton<IMLDatasetSource>(new FixedSource(new MLDatasetDefinition("other", [new("a", MLColumnRole.Feature)])));
        builder.Services.AddIyuML(o => o.Dataset(Readings).NoReadPolicies());
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("'readings'", refusal.Message);
    }

    [Fact]
    public async Task A_host_that_neither_registered_a_guard_nor_said_it_has_no_read_policies_does_not_start()
    {
        var builder = Host();
        builder.Services.AddSingleton<IMLDatasetSource>(new FixedSource(Readings));
        builder.Services.AddIyuML(o => o.Dataset(Readings));
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("IMLReadGuard", refusal.Message);
        Assert.Contains("NoReadPolicies", refusal.Message);
    }

    [Fact]
    public async Task A_host_that_registered_a_guard_and_also_said_it_has_no_read_policies_does_not_start()
    {
        var builder = Host();
        builder.Services.AddSingleton<IMLDatasetSource>(new FixedSource(Readings));
        builder.Services.AddSingleton<IMLReadGuard, FixedGuard>();
        builder.Services.AddIyuML(o => o.Dataset(Readings).NoReadPolicies());
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("choose between", refusal.Message);
    }

    [Fact]
    public async Task A_host_with_two_guards_does_not_start()
    {
        var builder = Host();
        builder.Services.AddSingleton<IMLDatasetSource>(new FixedSource(Readings));
        builder.Services.AddSingleton<IMLReadGuard, FixedGuard>();
        builder.Services.AddScoped<IMLReadGuard, FixedGuard>();
        builder.Services.AddIyuML(o => o.Dataset(Readings));
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("one IMLReadGuard, not 2", refusal.Message);
    }

    [Fact]
    public async Task Every_missing_piece_is_reported_at_once()
    {
        var builder = Host();
        builder.Services.AddIyuML(o => o.Dataset(Readings));
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("IMLDatasetSource", refusal.Message);
        Assert.Contains("IMLReadGuard", refusal.Message);
    }

    [Fact]
    public void A_dataset_name_declared_twice_ignoring_case_is_refused_when_declared()
    {
        var options = new IyuMLOptions().Dataset(Readings);

        Assert.Throws<ArgumentException>(() => options.Dataset(new MLDatasetDefinition("READINGS", [new("a", MLColumnRole.Feature)])));
    }

    [Fact]
    public async Task A_host_that_never_calls_AddIyuML_registers_nothing_of_the_layer()
    {
        var builder = Host();
        using var app = builder.Build();
        await app.StartAsync();

        Assert.Null(app.Services.GetService<IMLDatasetSource>());
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s.GetType().Namespace == "Iyu.ML");
        await app.StopAsync();
    }
}
