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
/// The settle path is where a request meets what the engine learns, so these tests are written against the request: each
/// one is something a caller might send, and the assertion is on what ends up stored.
/// </summary>
public sealed class IyuLowlineSettleServiceTests : IAsyncLifetime
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _provider;
    private readonly OrderHook _hook = new();

    public IyuLowlineSettleServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o => o.Engine.Form("order", OrderForm));
        services.AddScoped<ILowlineFormHook>(_ => _hook);
        _provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // A save can leave the engine choosing its thresholds again on a background worker; let it finish before the
        // file it writes to goes away.
        var forms = _provider.GetRequiredService<LowlineForms>();
        foreach (var space in new[] { "plant-a", "plant-b" })
            await forms.WhenTunedAsync(new LowlineScope("order", space));
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private static ClaimsPrincipal Person(string name) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], authenticationType: "test"));

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private async Task<IyuLowlineSettleOutcome> Settle(string form, string id, string? draft, ClaimsPrincipal user)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IyuLowlineSettleService>().SettleAsync(form, id, draft, user);
    }

    private async Task<IReadOnlyList<SettledRecord>> Stored(string space = "plant-a") =>
        await _provider.GetRequiredService<ILowlineStore>().ReadRecordsAsync(new LowlineScope("order", space));

    private static JsonElement Parse(SettledRecord record) => JsonDocument.Parse(record.Json).RootElement;

    [Fact]
    public async Task A_saved_record_is_settled_from_the_stored_row_in_the_hooks_space()
    {
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "red");

        var outcome = await Settle("order", "r1", draft: null, Person("alice"));

        Assert.Equal(IyuLowlineSettleStatus.Settled, outcome.Status);
        var record = Assert.Single(await Stored("plant-a"));
        Assert.Equal("red", Parse(record).GetProperty("values").GetProperty("colour").GetString());
        Assert.Equal("acme", Parse(record).GetProperty("values").GetProperty("customer").GetString());
        Assert.Empty(await Stored("plant-b"));
    }

    [Fact]
    public async Task What_the_draft_says_was_opened_or_shown_never_becomes_a_learned_value()
    {
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "red");
        var lie = """{"opened":{"colour":"blue","customer":"globex"},"fields":{"colour":{"shown":{"value":"blue"}}}}""";

        await Settle("order", "r1", lie, Person("alice"));

        var values = Parse(Assert.Single(await Stored())).GetProperty("values");
        Assert.Equal("red", values.GetProperty("colour").GetString());
        Assert.Equal("acme", values.GetProperty("customer").GetString());
    }

    [Fact]
    public async Task A_draft_cannot_sign_the_record_name_a_batch_or_claim_a_check()
    {
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "red");
        var forged = """{"opened":{},"fields":{"colour":{"held":0.5}},"by":"mallory","batch":"evil"}""";

        await Settle("order", "r1", forged, Person("alice"));

        var record = Parse(Assert.Single(await Stored()));
        Assert.False(record.TryGetProperty("settled_by", out _), record.GetRawText());
        Assert.False(record.TryGetProperty("batch", out _), record.GetRawText());
        Assert.Empty(record.GetProperty("probabilities").EnumerateObject());
        Assert.NotEqual("Check", record.GetProperty("settlements").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task A_caller_who_is_not_signed_in_settles_nothing_and_the_hook_is_not_asked()
    {
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "red");

        var outcome = await Settle("order", "r1", null, Anonymous);

        Assert.Equal(IyuLowlineSettleStatus.Unauthenticated, outcome.Status);
        Assert.Equal(0, _hook.Reads);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task A_record_that_is_not_stored_settles_nothing()
    {
        // The save was rolled back, or never happened: there is no row for the hook to read.
        var outcome = await Settle("order", "never-saved", null, Person("alice"));

        Assert.Equal(IyuLowlineSettleStatus.RecordNotFound, outcome.Status);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task Someone_elses_record_looks_the_same_as_a_missing_one_and_settles_nothing()
    {
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "red");

        var someoneElses = await Settle("order", "r1", null, Person("mallory"));
        var missing = await Settle("order", "no-such", null, Person("mallory"));

        Assert.Equal(IyuLowlineSettleStatus.RecordNotFound, someoneElses.Status);
        Assert.Equal(missing, someoneElses);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task A_stored_value_the_form_does_not_hold_is_refused_and_nothing_is_learned()
    {
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "green");

        var outcome = await Settle("order", "r1", null, Person("alice"));

        Assert.Equal(IyuLowlineSettleStatus.Refused, outcome.Status);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task A_draft_that_is_not_the_engines_format_is_refused_and_nothing_is_stored()
    {
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "red");

        var outcome = await Settle("order", "r1", "not json at all", Person("alice"));

        Assert.Equal(IyuLowlineSettleStatus.Refused, outcome.Status);
        Assert.Empty(await Stored());
    }

    [Fact]
    public async Task A_draft_over_the_limit_is_refused_before_the_hook_reads_anything()
    {
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "red");
        var huge = "{\"opened\":{},\"fields\":{},\"pad\":\"" + new string('x', 70_000) + "\"}";

        var outcome = await Settle("order", "r1", huge, Person("alice"));

        Assert.Equal(IyuLowlineSettleStatus.Refused, outcome.Status);
        Assert.Equal(0, _hook.Reads);
        Assert.Empty(await Stored());
    }

    [Theory]
    [InlineData("unknown-form")]
    [InlineData("")]
    public async Task A_form_that_is_not_defined_is_unknown(string form)
    {
        var outcome = await Settle(form, "r1", null, Person("alice"));

        Assert.Equal(IyuLowlineSettleStatus.FormUnknown, outcome.Status);
        Assert.Equal(0, _hook.Reads);
    }

    [Fact]
    public async Task Settling_a_record_again_replaces_what_the_first_save_taught()
    {
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "red");
        await Settle("order", "r1", null, Person("alice"));
        _hook.Save("r1", owner: "alice", space: "plant-a", customer: "acme", colour: "blue");
        await Settle("order", "r1", null, Person("alice"));

        var record = Assert.Single(await Stored());
        Assert.Equal("blue", Parse(record).GetProperty("values").GetProperty("colour").GetString());
    }
}
