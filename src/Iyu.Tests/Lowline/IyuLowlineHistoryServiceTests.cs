using Iyu.Lowline;
using Lowline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// The history pass brings an application's older records to the engine as typed by a person, so the first day is not empty.
/// The measured fact it stands on - history handed over as the application's own doing makes no suggestion - is pinned in
/// <see cref="LowlineHistoryEffectTests"/>.
/// </summary>
public sealed class IyuLowlineHistoryServiceTests : IAsyncLifetime
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    private static readonly LowlineScope PlantA = new("order", "plant-a");

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");
    private readonly HistorySource _source = new();
    private ServiceProvider _provider = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is null) return;
        await _provider.GetRequiredService<LowlineForms>().WhenTunedAsync(PlantA);
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private void Start(int batchSize = 500, bool withSource = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o =>
        {
            o.Engine.Form("order", OrderForm);
            o.HistoryBatchSize = batchSize;
        });
        if (withSource) services.AddScoped<ILowlineHistorySource>(_ => _source);
        _provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();
    }

    private Task<IyuLowlineHistoryReport> Run() =>
        _provider.GetRequiredService<IyuLowlineHistoryService>().RunOnceAsync();

    private async Task<IReadOnlyList<string>> HeldIds() =>
        await _provider.GetRequiredService<ILowlineRecordIndex>().ListRecordIdsAsync(PlantA);

    private async Task<bool> Suggests()
    {
        var forms = _provider.GetRequiredService<LowlineForms>();
        await forms.WhenTunedAsync(PlantA);
        var shown = await forms.ProposeAsync(
            PlantA, "new", new Dictionary<string, FieldValue> { ["customer"] = "acme" }, allowed: null);
        return shown.Single(p => p.Field == "colour").Suggestion is not null;
    }

    [Fact]
    public async Task History_the_application_declares_is_learned_and_the_first_day_has_a_suggestion()
    {
        for (var i = 0; i < 25; i++) _source.Add("plant-a", $"h{i:000}", "acme", "red");
        Start();
        Assert.False(await Suggests());

        var report = await Run();

        Assert.Equal(25, report.Learned);
        Assert.Equal(0, report.Refused);
        Assert.Equal(25, (await HeldIds()).Count);
        Assert.True(await Suggests());
    }

    [Fact]
    public async Task A_large_history_arrives_a_batch_at_a_time_and_a_finished_one_costs_nothing()
    {
        for (var i = 0; i < 25; i++) _source.Add("plant-a", $"h{i:000}", "acme", "red");
        Start(batchSize: 10);

        Assert.Equal(10, (await Run()).Learned);
        Assert.Equal(10, (await Run()).Learned);
        Assert.Equal(5, (await Run()).Learned);
        var done = await Run();

        Assert.Equal(0, done.Learned);
        Assert.Equal(25, (await HeldIds()).Count);
        Assert.Equal(3, _source.Reads); // the finished pass read no records
    }

    [Fact]
    public async Task A_record_the_store_already_holds_is_not_learned_again()
    {
        _source.Add("plant-a", "h1", "acme", "red");
        _source.Add("plant-a", "h2", "acme", "red");
        Start();
        var forms = _provider.GetRequiredService<LowlineForms>();
        // h1 was already settled by a person through the settle service - the pass must leave it be.
        await forms.SettleAsync(
            PlantA, "h1", new Dictionary<string, FieldValue> { ["customer"] = "acme", ["colour"] = "blue" },
            draft: null, SettledBy.Person, new Attestation());

        var report = await Run();

        Assert.Equal(1, report.Learned);
        Assert.Equal(["h1", "h2"], (await HeldIds()).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_record_the_engine_refuses_is_counted_and_the_rest_are_learned()
    {
        _source.Add("plant-a", "a-bad", "acme", "green"); // not one of the form's choices
        _source.Add("plant-a", "b-ok", "acme", "red");
        Start();

        var first = await Run();
        var second = await Run();

        Assert.Equal(1, first.Learned);
        Assert.Equal(1, first.Refused);
        Assert.Equal(0, second.Refused); // not tried again, so it does not hold the front of the batch
        Assert.Equal(["b-ok"], await HeldIds());
    }

    [Fact]
    public async Task A_space_that_cannot_be_read_does_not_keep_the_others_from_being_learned()
    {
        _source.Add("plant-a", "h1", "acme", "red");
        _source.Add("plant-b", "h1", "acme", "red");
        _source.FailingSpace = "plant-a";
        Start();

        var report = await Run();

        Assert.Equal(1, report.Learned);
        Assert.Single(report.Skipped);
        Assert.Contains("plant-a", report.Skipped[0]);
        await _provider.GetRequiredService<LowlineForms>().WhenTunedAsync(new LowlineScope("order", "plant-b"));
    }

    [Fact]
    public async Task A_form_with_no_history_source_is_left_alone()
    {
        _source.Add("plant-a", "h1", "acme", "red");
        Start(withSource: false);

        var report = await Run();

        Assert.Equal(0, report.Learned);
        Assert.Empty(await HeldIds());
    }

    private sealed class HistorySource : ILowlineHistorySource
    {
        private readonly Dictionary<string, Dictionary<string, LowlineHistoryRecord>> _rows = [];

        public string Form => "order";

        public string? FailingSpace { get; set; }

        public int Reads { get; private set; }

        public void Add(string space, string id, string customer, string colour)
        {
            if (!_rows.TryGetValue(space, out var rows)) _rows[space] = rows = [];
            rows[id] = new LowlineHistoryRecord(id, new Dictionary<string, FieldValue> { ["customer"] = customer, ["colour"] = colour });
        }

        public Task<IReadOnlyList<string>> ListHistorySpacesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(_rows.Keys.ToList());

        public Task<IReadOnlySet<string>> ListHistoryKeysAsync(string space, CancellationToken cancellationToken)
        {
            if (space == FailingSpace) throw new InvalidOperationException("the source for this space is unreachable");
            return Task.FromResult<IReadOnlySet<string>>(_rows[space].Keys.ToHashSet());
        }

        public Task<IReadOnlyList<LowlineHistoryRecord>> ReadHistoryAsync(
            string space, IReadOnlyCollection<string> recordIds, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<LowlineHistoryRecord>>(
                recordIds.Where(_rows[space].ContainsKey).Select(id => _rows[space][id]).ToList());
        }
    }
}
