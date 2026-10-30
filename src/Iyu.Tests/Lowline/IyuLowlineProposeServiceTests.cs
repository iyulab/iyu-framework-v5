using System.Security.Claims;
using System.Text.Json;
using Iyu.Lowline;
using Lowline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// The proposal path for a host that does not use the mapped route: it hands the service the values it derived itself, and
/// must get the same hold-back, the same check at settle and the same limit the route gives.
/// </summary>
public sealed class IyuLowlineProposeServiceTests : IAsyncLifetime
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    private const string Salt = "0123456789abcdef0123456789abcdef-installation-salt";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");
    private readonly OrderHook _hook = new();
    private ServiceProvider _provider = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is null) return;
        await _provider.GetRequiredService<LowlineForms>().WhenTunedAsync(new LowlineScope("order", "plant-a"));
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private static ClaimsPrincipal Person(string name) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.NameIdentifier, "account-" + name)], authenticationType: "test"));

    /// <summary>Starts a host with no route at all, whose every roll is <paramref name="roll"/>, and settles forty acme orders as red.</summary>
    private async Task StartAsync(double roll, int? maxPending = null, ILowlineFormHook? hook = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddSingleton<ILowlineRoll>(new FixedRoll(roll));
        services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o =>
        {
            o.Engine.Form("order", OrderForm);
            o.Settler.Salt = Salt;
            if (maxPending is { } limit) o.MaxPendingChecks = limit;
        });
        services.AddScoped<ILowlineFormHook>(_ => hook ?? _hook);
        _provider = services.BuildServiceProvider(validateScopes: true);
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();

        for (var i = 0; i < 40; i++)
        {
            _hook.Save($"seed{i}", "alice", "plant-a", "acme", "red");
            Assert.True((await Settle($"seed{i}")).Settled);
        }

        await _provider.GetRequiredService<LowlineForms>().WhenTunedAsync(new LowlineScope("order", "plant-a"));
    }

    private async Task<IyuLowlineProposeOutcome> Propose(string id, string customer = "acme", string form = "order")
    {
        await using var scope = _provider.CreateAsyncScope();
        var values = new Dictionary<string, FieldValue> { ["customer"] = customer };
        return await scope.ServiceProvider.GetRequiredService<IyuLowlineProposeService>()
            .ProposeAsync(form, id, values, Person("alice"));
    }

    private async Task<IyuLowlineSettleOutcome> Settle(string id)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IyuLowlineSettleService>()
            .SettleAsync("order", id, draft: null, Person("alice"));
    }

    private async Task<JsonElement> Stored(string id) =>
        JsonDocument.Parse((await _provider.GetRequiredService<ILowlineStore>().ReadRecordsAsync(new LowlineScope("order", "plant-a")))
            .Single(r => r.Id == id).Json).RootElement;

    private static bool HasSuggestion(Proposal proposal) => proposal.Suggestion is not null;

    [Fact]
    public async Task Values_the_host_derived_get_an_answer_and_a_drawn_field_has_it_held_back()
    {
        await StartAsync(roll: 0.99);
        var answered = await Propose("answered");
        Assert.Equal(IyuLowlineProposeStatus.Proposed, answered.Status);
        Assert.True(HasSuggestion(answered.Proposals!.Single(p => p.Field == "colour")));

        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
        _provider = null!;

        await StartAsync(roll: 0);
        var held = await Propose("held");

        Assert.Equal(IyuLowlineProposeStatus.Proposed, held.Status);
        Assert.False(HasSuggestion(held.Proposals!.Single(p => p.Field == "colour")));
    }

    [Fact]
    public async Task A_settle_after_a_service_proposal_finds_the_draw_and_records_a_check()
    {
        await StartAsync(roll: 0);
        await Propose("new");

        _hook.Save("new", "alice", "plant-a", "acme", "red");
        Assert.True((await Settle("new")).Settled);

        var record = await Stored("new");
        Assert.Equal("Check", record.GetProperty("settlements").GetProperty("colour").GetString());
        Assert.Equal("red", record.GetProperty("suggested").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task A_proposal_past_the_limit_is_refused_with_the_time_until_a_place_is_free()
    {
        await StartAsync(roll: 0, maxPending: 1);
        Assert.Equal(IyuLowlineProposeStatus.Proposed, (await Propose("first")).Status);

        var second = await Propose("second");

        Assert.Equal(IyuLowlineProposeStatus.CheckBacklog, second.Status);
        Assert.Null(second.Proposals);
        Assert.True(second.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task An_unknown_form_and_a_record_the_hook_will_not_open_are_told_apart_from_a_proposal()
    {
        await StartAsync(roll: 0);
        Assert.Equal(IyuLowlineProposeStatus.FormUnknown, (await Propose("x", form: "nothing")).Status);

        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
        _provider = null!;

        await StartAsync(roll: 0, hook: new ClosedHook(_hook));
        var closed = await Propose("x");
        Assert.Equal(IyuLowlineProposeStatus.RecordNotFound, closed.Status);
        Assert.Null(closed.Proposals);
    }

    [Fact]
    public async Task A_missing_record_id_is_refused()
    {
        await StartAsync(roll: 0);

        var outcome = await Propose(" ");

        Assert.Equal(IyuLowlineProposeStatus.Refused, outcome.Status);
        Assert.NotNull(outcome.Detail);
    }

    /// <summary>A hook that answers every <see cref="ILowlineFormHook.OpenAsync"/> with "not yours" and reads through another.</summary>
    private sealed class ClosedHook(OrderHook inner) : ILowlineFormHook
    {
        public string Form => "order";

        public Task<LowlineFormContext?> OpenAsync(string recordId, ClaimsPrincipal user, CancellationToken cancellationToken) =>
            Task.FromResult<LowlineFormContext?>(null);

        public Task<LowlineSavedRecord?> ReadSavedAsync(string recordId, ClaimsPrincipal user, CancellationToken cancellationToken) =>
            inner.ReadSavedAsync(recordId, user, cancellationToken);
    }

    private sealed class FixedRoll(double value) : ILowlineRoll
    {
        public double Roll(string salt, string form, string space, string recordId, string field) => value;
    }
}
