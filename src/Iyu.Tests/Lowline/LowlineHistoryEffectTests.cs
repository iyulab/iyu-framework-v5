using Iyu.Lowline;
using Lowline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// What history the engine learns from. A host that brings records it already holds to a new installation has to hand the
/// engine something it counts, or the first suggestions never come. These tests pin what the engine does today with the two
/// ways such a record can be handed over, so a change in the engine shows up here instead of in a host that built on it.
/// </summary>
public sealed class LowlineHistoryEffectTests : IAsyncLifetime
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    private const int History = 25;

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o => o.Engine.Form("order", OrderForm));
        _provider = services.BuildServiceProvider(validateScopes: true);
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var forms = _provider.GetRequiredService<LowlineForms>();
        foreach (var space in new[] { "none", "application", "person" })
            await forms.WhenTunedAsync(new LowlineScope("order", space));
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    /// <summary>Hands the engine <see cref="History"/> orders for acme, all red, the way a host's import would.</summary>
    private async Task LearnAsync(string space, SettledBy by)
    {
        var forms = _provider.GetRequiredService<LowlineForms>();
        var scope = new LowlineScope("order", space);
        for (var i = 0; i < History; i++)
        {
            var values = new Dictionary<string, FieldValue> { ["customer"] = "acme", ["colour"] = "red" };
            await forms.SettleAsync(scope, $"history{i}", values, draft: null, by, new Attestation());
        }

        await forms.WhenTunedAsync(scope);
    }

    private async Task<bool> SuggestsAsync(string space)
    {
        var forms = _provider.GetRequiredService<LowlineForms>();
        var values = new Dictionary<string, FieldValue> { ["customer"] = "acme" };
        var shown = await forms.ProposeAsync(new LowlineScope("order", space), "new", values, allowed: null);
        return shown.Single(p => p.Field == "colour").Suggestion is not null;
    }

    [Fact]
    public async Task Without_history_nothing_is_suggested()
    {
        Assert.False(await SuggestsAsync("none"));
    }

    [Fact]
    public async Task History_handed_over_as_application_settled_does_not_make_a_suggestion()
    {
        await LearnAsync("application", SettledBy.Application);

        Assert.False(await SuggestsAsync("application"));
    }

    [Fact]
    public async Task History_handed_over_as_typed_by_a_person_makes_a_suggestion()
    {
        await LearnAsync("person", SettledBy.Person);

        Assert.True(await SuggestsAsync("person"));
    }
}
