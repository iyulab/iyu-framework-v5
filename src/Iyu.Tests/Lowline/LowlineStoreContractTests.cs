using Iyu.Lowline;
using Lowline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// What any <see cref="ILowlineStore"/> must do, written once and run against the engine's own reference
/// (<see cref="InMemoryLowlineStore"/>) and against the application-table store. Where the two disagree, the reference is
/// the contract and the store is the one to fix.
/// </summary>
public abstract class LowlineStoreContractTests : IDisposable
{
    private static readonly LowlineScope Orders = new("order", "plant-a");
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    protected abstract ILowlineStore Store { get; }

    public virtual void Dispose() => GC.SuppressFinalize(this);

    private static SettledRecord Rec(string id, DateTimeOffset at, string json = "{}") => new(id, at, json);

    [Fact]
    public async Task Records_read_back_in_settlement_order_whatever_order_they_were_added_in()
    {
        await Store.AddRecordAsync(Orders, Rec("c", T0.AddMinutes(3)));
        await Store.AddRecordAsync(Orders, Rec("a", T0.AddMinutes(1)));
        await Store.AddRecordAsync(Orders, Rec("b", T0.AddMinutes(2)));

        var read = await Store.ReadRecordsAsync(Orders);

        Assert.Equal(["a", "b", "c"], read.Select(r => r.Id));
        Assert.Equal(T0.AddMinutes(1), read[0].SettledAt);
    }

    [Fact]
    public async Task Records_settled_at_one_instant_read_in_a_stable_order()
    {
        await Store.AddRecordAsync(Orders, Rec("b", T0));
        await Store.AddRecordAsync(Orders, Rec("a", T0));

        var first = (await Store.ReadRecordsAsync(Orders)).Select(r => r.Id).ToList();
        var second = (await Store.ReadRecordsAsync(Orders)).Select(r => r.Id).ToList();

        // Which of two records settled at one instant comes first is not part of the contract — the reference store
        // keeps insertion order, a table has none to keep — but the order must not change between reads.
        Assert.Equal(first, second);
        Assert.Equal(["a", "b"], first.OrderBy(i => i, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_second_save_of_one_id_replaces_the_first()
    {
        await Store.AddRecordAsync(Orders, Rec("a", T0, "{\"v\":1}"));
        await Store.AddRecordAsync(Orders, Rec("a", T0.AddHours(1), "{\"v\":2}"));

        var read = await Store.ReadRecordsAsync(Orders);

        var only = Assert.Single(read);
        Assert.Equal("{\"v\":2}", only.Json);
        Assert.Equal(T0.AddHours(1), only.SettledAt);
    }

    [Fact]
    public async Task The_record_line_comes_back_exactly_as_it_was_given()
    {
        var line = "{\"id\":\"a\",\"note\":\"quote \\\" slash \\\\ 한글 \\u00e9\",\"n\":[1,2.50]}";
        await Store.AddRecordAsync(Orders, Rec("a", T0, line));

        Assert.Equal(line, Assert.Single(await Store.ReadRecordsAsync(Orders)).Json);
    }

    [Fact]
    public async Task Scopes_keep_their_records_apart_by_form_and_by_space()
    {
        await Store.AddRecordAsync(Orders, Rec("a", T0));
        await Store.AddRecordAsync(new LowlineScope("order", "plant-b"), Rec("b", T0));
        await Store.AddRecordAsync(new LowlineScope("invoice", "plant-a"), Rec("c", T0));

        Assert.Equal(["a"], (await Store.ReadRecordsAsync(Orders)).Select(r => r.Id));
        Assert.Equal(["b"], (await Store.ReadRecordsAsync(new LowlineScope("order", "plant-b"))).Select(r => r.Id));
        Assert.Empty(await Store.ReadRecordsAsync(new LowlineScope("order", "plant-c")));
    }

    [Fact]
    public async Task The_same_id_in_two_scopes_is_two_records()
    {
        await Store.AddRecordAsync(Orders, Rec("a", T0, "{\"s\":\"a\"}"));
        await Store.AddRecordAsync(new LowlineScope("order", "plant-b"), Rec("a", T0, "{\"s\":\"b\"}"));

        Assert.Equal("{\"s\":\"a\"}", Assert.Single(await Store.ReadRecordsAsync(Orders)).Json);
        Assert.True(await Store.ForgetAsync(Orders, "a"));
        Assert.Equal("{\"s\":\"b\"}", Assert.Single(await Store.ReadRecordsAsync(new LowlineScope("order", "plant-b"))).Json);
    }

    [Fact]
    public async Task Forgetting_a_record_removes_it_and_says_whether_it_held_one()
    {
        await Store.AddRecordAsync(Orders, Rec("a", T0));
        await Store.AddRecordAsync(Orders, Rec("b", T0.AddMinutes(1)));

        Assert.True(await Store.ForgetAsync(Orders, "a"));
        Assert.False(await Store.ForgetAsync(Orders, "a"));
        Assert.False(await Store.ForgetAsync(Orders, "never-held"));

        Assert.Equal(["b"], (await Store.ReadRecordsAsync(Orders)).Select(r => r.Id));
    }

    [Fact]
    public async Task Forgetting_before_a_time_removes_the_older_records_and_gives_their_ids()
    {
        await Store.AddRecordAsync(Orders, Rec("old1", T0));
        await Store.AddRecordAsync(Orders, Rec("old2", T0.AddDays(1)));
        await Store.AddRecordAsync(Orders, Rec("new", T0.AddDays(10)));
        await Store.AddRecordAsync(new LowlineScope("order", "plant-b"), Rec("other-old", T0));

        var gone = await Store.ForgetBeforeAsync(Orders, T0.AddDays(5));

        Assert.Equal(["old1", "old2"], gone.OrderBy(i => i, StringComparer.Ordinal));
        Assert.Equal(["new"], (await Store.ReadRecordsAsync(Orders)).Select(r => r.Id));
        Assert.Equal(["other-old"], (await Store.ReadRecordsAsync(new LowlineScope("order", "plant-b"))).Select(r => r.Id));
    }

    [Fact]
    public async Task Forgetting_before_the_instant_a_record_was_settled_at_keeps_it()
    {
        await Store.AddRecordAsync(Orders, Rec("edge", T0));

        Assert.Empty(await Store.ForgetBeforeAsync(Orders, T0));
        Assert.Single(await Store.ReadRecordsAsync(Orders));
    }

    [Fact]
    public async Task There_is_no_tuning_until_one_is_kept_and_a_new_one_replaces_the_old()
    {
        Assert.Null(await Store.ReadTuningAsync(Orders));

        await Store.WriteTuningAsync(Orders, "{\"t\":1}");
        await Store.WriteTuningAsync(Orders, "{\"t\":2}");

        Assert.Equal("{\"t\":2}", await Store.ReadTuningAsync(Orders));
        Assert.Null(await Store.ReadTuningAsync(new LowlineScope("order", "plant-b")));
    }

    [Fact]
    public async Task Forgetting_records_leaves_the_scopes_tuning()
    {
        await Store.AddRecordAsync(Orders, Rec("a", T0));
        await Store.WriteTuningAsync(Orders, "{\"t\":1}");

        await Store.ForgetAsync(Orders, "a");
        await Store.ForgetBeforeAsync(Orders, T0.AddYears(1));

        Assert.Equal("{\"t\":1}", await Store.ReadTuningAsync(Orders));
    }
}

public sealed class InMemoryLowlineStoreContractTests : LowlineStoreContractTests
{
    protected override ILowlineStore Store { get; } = new InMemoryLowlineStore();
}

public sealed class EfLowlineStoreContractTests : LowlineStoreContractTests
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public EfLowlineStoreContractTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<LowlineTestDb>(o => o.UseSqlite(_connection));
        services.AddSingleton<ILowlineStore, EfLowlineStore<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>>();
        _provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();
    }

    protected override ILowlineStore Store => _provider.GetRequiredService<ILowlineStore>();

    public override void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
        base.Dispose();
    }
}

/// <summary>An application's two Lowline tables, declared the way a consumer schema would declare them.</summary>
public sealed class LowlineTestRecord : ILowlineRecordRow
{
    public string Form { get; set; } = "";
    public string Space { get; set; } = "";
    public string RecordId { get; set; } = "";
    public DateTimeOffset SettledAt { get; set; }
    public string Json { get; set; } = "";
}

public sealed class LowlineTestTuning : ILowlineTuningRow
{
    public string Form { get; set; } = "";
    public string Space { get; set; } = "";
    public string Tuning { get; set; } = "";
}

public sealed class LowlineTestDb(DbContextOptions<LowlineTestDb> options) : DbContext(options)
{
    public DbSet<LowlineTestRecord> Records => Set<LowlineTestRecord>();
    public DbSet<LowlineTestTuning> Tunings => Set<LowlineTestTuning>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LowlineTestRecord>().HasKey(r => new { r.Form, r.Space, r.RecordId });
        modelBuilder.Entity<LowlineTestTuning>().HasKey(t => new { t.Form, t.Space });
    }
}
