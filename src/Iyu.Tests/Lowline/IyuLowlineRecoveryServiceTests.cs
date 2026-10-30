using System.Security.Claims;
using Iyu.Lowline;
using Lowline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// "Delete means forget" is a comparison, not a watcher: these tests delete sources in the hook and check what the pass
/// does to the store and to the running engine, including the ways it could forget something it should not.
/// </summary>
public sealed class IyuLowlineRecoveryServiceTests : IAsyncLifetime
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    private static readonly LowlineScope PlantA = new("order", "plant-a");
    private static readonly ClaimsPrincipal Alice = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");
    private readonly OrderHook _hook = new() { CanList = true };
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturedLogs _logs = new();
    private ServiceProvider _provider = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is null) return;
        var forms = _provider.GetRequiredService<LowlineForms>();
        foreach (var space in new[] { "plant-a", "plant-b" })
            await forms.WhenTunedAsync(new LowlineScope("order", space));
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private void Build(Action<IyuLowlineOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(_logs));
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o =>
        {
            o.Engine.Form("order", OrderForm);
            configure?.Invoke(o);
        });
        services.AddScoped<ILowlineFormHook>(_ => _hook);
        _provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();
    }

    private async Task Settle(string id)
    {
        await using var scope = _provider.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IyuLowlineSettleService>().SettleAsync("order", id, null, Alice);
        Assert.True(outcome.Settled, outcome.Status.ToString());
    }

    private Task<IyuLowlineRecoveryReport> Recover() => _provider.GetRequiredService<IyuLowlineRecoveryService>().RunOnceAsync();

    private async Task<string[]> StoredIds(string space = "plant-a") =>
        (await _provider.GetRequiredService<ILowlineStore>().ReadRecordsAsync(new LowlineScope("order", space)))
        .Select(r => r.Id).Order(StringComparer.Ordinal).ToArray();

    private async Task Seed(string prefix, int count, string customer, string colour, string space = "plant-a")
    {
        for (var i = 0; i < count; i++)
        {
            _hook.Save($"{prefix}{i}", "alice", space, customer, colour);
            await Settle($"{prefix}{i}");
        }
    }

    [Fact]
    public async Task A_record_whose_source_was_deleted_is_forgotten_and_the_others_are_kept()
    {
        Build();
        await Seed("r", 4, "acme", "red");
        _hook.Delete("r1");

        var report = await Recover();

        Assert.Equal(1, report.Forgotten);
        Assert.Equal(["r0", "r2", "r3"], await StoredIds());
    }

    [Fact]
    public async Task A_pass_over_records_whose_sources_all_exist_forgets_nothing()
    {
        Build();
        await Seed("r", 3, "acme", "red");

        Assert.Equal(0, (await Recover()).Forgotten);
        Assert.Equal(["r0", "r1", "r2"], await StoredIds());
    }

    [Fact]
    public async Task The_running_engine_stops_answering_from_a_forgotten_record()
    {
        Build();
        await Seed("a", 40, "acme", "red");
        await Seed("g", 40, "globex", "blue");
        var forms = _provider.GetRequiredService<LowlineForms>();
        await forms.WhenTunedAsync(PlantA);
        var acme = new Dictionary<string, FieldValue> { ["customer"] = "acme" };
        var before = await forms.ProposeAsync(PlantA, "new", acme, allowed: null);
        Assert.Equal("red", before.Single(p => p.Field == "colour").Suggestion?.Value);

        for (var i = 0; i < 40; i++) _hook.Delete($"a{i}");
        var report = await Recover();
        await forms.WhenTunedAsync(PlantA);

        Assert.Equal(40, report.Forgotten);
        var after = await forms.ProposeAsync(PlantA, "new", acme, allowed: null);
        Assert.Null(after.Single(p => p.Field == "colour").Suggestion);
    }

    [Fact]
    public async Task A_record_settled_while_the_pass_is_listing_sources_is_not_mistaken_for_a_deleted_one()
    {
        Build();
        await Seed("r", 3, "acme", "red");
        // The listing was taken before "late" existed; "late" is saved and settled before the pass compares.
        _hook.DuringList = async () =>
        {
            _hook.Save("late", "alice", "plant-a", "acme", "red");
            await Settle("late");
        };

        var report = await Recover();

        Assert.Equal(0, report.Forgotten);
        Assert.Contains("late", await StoredIds());
    }

    [Fact]
    public async Task A_listing_that_came_back_empty_forgets_nothing_and_is_reported()
    {
        Build();
        await Seed("r", 20, "acme", "red");
        for (var i = 0; i < 20; i++) _hook.Delete($"r{i}");

        var report = await Recover();

        Assert.Equal(0, report.Forgotten);
        Assert.Contains("order/plant-a", Assert.Single(report.Skipped));
        Assert.Equal(20, (await StoredIds()).Length);
    }

    [Fact]
    public async Task With_the_guard_lifted_an_emptied_source_forgets_everything()
    {
        Build(o => o.MaxForgetFraction = 1);
        await Seed("r", 20, "acme", "red");
        for (var i = 0; i < 20; i++) _hook.Delete($"r{i}");

        Assert.Equal(20, (await Recover()).Forgotten);
        Assert.Empty(await StoredIds());
    }

    [Fact]
    public async Task A_form_whose_hook_cannot_list_its_sources_is_left_alone()
    {
        Build();
        await Seed("r", 3, "acme", "red");
        _hook.Delete("r0");
        _hook.CanList = false;

        Assert.Equal(0, (await Recover()).Forgotten);
        Assert.Equal(3, (await StoredIds()).Length);
    }

    [Fact]
    public async Task Only_the_scope_whose_source_went_is_touched()
    {
        Build();
        await Seed("a", 3, "acme", "red", space: "plant-a");
        await Seed("b", 3, "acme", "red", space: "plant-b");
        _hook.Delete("a0");

        await Recover();

        Assert.Equal(["a1", "a2"], await StoredIds("plant-a"));
        Assert.Equal(["b0", "b1", "b2"], await StoredIds("plant-b"));
    }

    [Fact]
    public async Task A_scope_that_cannot_be_recovered_does_not_keep_the_others_from_being_recovered()
    {
        Build();
        await Seed("a", 3, "acme", "red", space: "plant-a");
        await Seed("b", 3, "acme", "red", space: "plant-b");
        _hook.Delete("a0");
        _hook.Delete("b0");
        _hook.ListingFailsFor.Add("plant-a");

        var report = await Recover();

        // plant-b is recovered whichever order the scopes come in.
        Assert.Equal(1, report.Forgotten);
        Assert.Equal(["b1", "b2"], await StoredIds("plant-b"));
        Assert.Equal(["a0", "a1", "a2"], await StoredIds("plant-a"));
        Assert.Contains("order/plant-a", Assert.Single(report.Skipped));
    }

    [Fact]
    public async Task Records_of_a_form_this_host_no_longer_defines_are_left_alone()
    {
        Build();
        var store = _provider.GetRequiredService<ILowlineStore>();
        var retired = new LowlineScope("retired-form", "plant-a");
        await store.AddRecordAsync(retired, new SettledRecord("x", _time.GetUtcNow(), "{}"));

        Assert.Equal(0, (await Recover()).Forgotten);
        Assert.Single(await store.ReadRecordsAsync(retired));
    }

    [Fact]
    public async Task The_worker_runs_a_pass_every_interval_and_not_before()
    {
        Build(o => o.RecoveryInterval = TimeSpan.FromMinutes(10));
        await Seed("r", 3, "acme", "red");
        _hook.Delete("r1");
        var workers = _provider.GetServices<IHostedService>().ToList();
        foreach (var worker in workers) await worker.StartAsync(CancellationToken.None);
        try
        {
            // The worker's timer counts from the moment it is created, which can be after StartAsync returns; advancing
            // the clock before then would move the first tick out of the window this test looks at.
            await Eventually(() => Task.FromResult(_logs.ToString().Contains("recovery worker started")), _logs);
            _time.Advance(TimeSpan.FromMinutes(9));
            await Task.Delay(300);
            Assert.Equal(3, (await StoredIds()).Length);

            _time.Advance(TimeSpan.FromMinutes(1));
            await Eventually(async () => (await StoredIds()).Length == 2, _logs);
            Assert.Equal(["r0", "r2"], await StoredIds());
        }
        finally
        {
            foreach (var worker in workers) await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_zero_interval_turns_the_worker_off()
    {
        Build(o => o.RecoveryInterval = TimeSpan.Zero);
        await Seed("r", 3, "acme", "red");
        _hook.Delete("r1");
        var workers = _provider.GetServices<IHostedService>().ToList();
        foreach (var worker in workers) await worker.StartAsync(CancellationToken.None);
        try
        {
            _time.Advance(TimeSpan.FromDays(1));
            await Task.Delay(300);
            Assert.Equal(3, (await StoredIds()).Length);
        }
        finally
        {
            foreach (var worker in workers) await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task Eventually(Func<Task<bool>> condition, CapturedLogs logs)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        Assert.True(await condition(), "the condition did not hold within five seconds; logged: " + logs);
    }

    /// <summary>Keeps what the service logged, so a worker that fails quietly says so in the failing assertion.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public ILogger CreateLogger(string categoryName) => new Capture(_lines);

        public void Dispose()
        {
        }

        public override string ToString() => string.Join(" | ", _lines);

        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception?.GetType().Name} {exception?.Message}");
        }
    }
}
