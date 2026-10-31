using Iyu.Lowline;
using Iyu.Lowline.EntityFramework;
using Lowline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// The host package assumes no storage technology: it asks for two ports and an Entity Framework host gets them from
/// the separate package. These tests hold both halves of that — a host with its own storage starts without any
/// Entity Framework registration, and the host package's assembly does not reference what only the adapter uses.
/// </summary>
public sealed class AddIyuLowlinePortsTests
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    /// <summary>A host's own index: this one holds nothing, which is a valid answer to both questions.</summary>
    private sealed class EmptyRecordIndex : ILowlineRecordIndex
    {
        public Task<IReadOnlyList<LowlineScope>> ListScopesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LowlineScope>>([]);

        public Task<IReadOnlyList<string>> ListRecordIdsAsync(LowlineScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static WebApplicationBuilder Host()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        return builder;
    }

    [Fact]
    public async Task A_host_with_its_own_storage_starts_over_the_two_ports_it_registered()
    {
        var store = new InMemoryLowlineStore();
        var builder = Host();
        builder.Services.AddSingleton<ILowlineStore>(store);
        builder.Services.AddSingleton<ILowlineRecordIndex, EmptyRecordIndex>();
        builder.Services.AddIyuLowline(o => o.Engine.Form("order", OrderForm));
        await using var app = builder.Build();
        await app.StartAsync();

        Assert.Same(store, app.Services.GetRequiredService<ILowlineStore>());

        var scope = new LowlineScope("order", "plant-a");
        await app.Services.GetRequiredService<LowlineForms>().SettleAsync(
            scope, "r1",
            new Dictionary<string, FieldValue> { ["customer"] = "acme", ["colour"] = "red" },
            draft: null, SettledBy.Person, attestation: null);

        // The record landed in the host's own store — nothing else holds it.
        Assert.Equal(["r1"], (await store.ReadRecordsAsync(scope)).Select(r => r.Id));
    }

    [Theory]
    [InlineData(true, false, "ILowlineRecordIndex")]
    [InlineData(false, true, "ILowlineStore")]
    [InlineData(false, false, "ILowlineStore and ILowlineRecordIndex")]
    public async Task A_host_that_registered_a_port_short_does_not_start_and_the_message_names_which(
        bool withStore, bool withIndex, string missing)
    {
        var builder = Host();
        if (withStore) builder.Services.AddSingleton<ILowlineStore>(new InMemoryLowlineStore());
        if (withIndex) builder.Services.AddSingleton<ILowlineRecordIndex, EmptyRecordIndex>();
        builder.Services.AddIyuLowline(o => o.Engine.Form("order", OrderForm));
        await using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains($"register {missing}.", refusal.Message);
        Assert.Contains("Iyu.Lowline.EntityFramework", refusal.Message);
    }

    [Fact]
    public void The_host_package_references_no_Entity_Framework_and_no_Iyu_Core_and_the_adapter_references_both()
    {
        static List<string> References(Type any) => any.Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

        var host = References(typeof(IyuLowlineOptions));
        Assert.DoesNotContain(host, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain("Iyu.Core", host);

        var adapter = References(typeof(IyuLowlineEntityFrameworkExtensions));
        Assert.Contains("Microsoft.EntityFrameworkCore", adapter);
        Assert.Contains("Iyu.Core", adapter);
        Assert.Contains("Iyu.Lowline", adapter);
    }
}
