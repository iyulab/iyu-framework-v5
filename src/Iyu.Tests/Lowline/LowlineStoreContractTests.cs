using Iyu.Lowline.EntityFrameworkCore;
using Iyu.Core.Lowline;
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
    public async Task A_second_save_of_one_id_keeps_the_first_line_beside_it()
    {
        await Store.AddRecordAsync(Orders, Rec("a", T0, "{\"v\":1}"));
        await Store.AddRecordAsync(Orders, Rec("a", T0.AddHours(1), "{\"v\":2}"));

        var read = await Store.ReadRecordsAsync(Orders);

        // The engine learns a record from its latest line and counts a later check as an audit of the decision first
        // settled, which the earlier line is — so neither line may be lost.
        Assert.Equal(["{\"v\":1}", "{\"v\":2}"], read.Select(r => r.Json));
        Assert.Equal([T0, T0.AddHours(1)], read.Select(r => r.SettledAt));
        Assert.All(read, r => Assert.Equal("a", r.Id));
    }

    [Fact]
    public async Task The_lines_of_several_records_read_back_in_settlement_order_together()
    {
        await Store.AddRecordAsync(Orders, Rec("a", T0, "{\"n\":1}"));
        await Store.AddRecordAsync(Orders, Rec("b", T0.AddMinutes(1), "{\"n\":2}"));
        await Store.AddRecordAsync(Orders, Rec("a", T0.AddMinutes(2), "{\"n\":3}"));

        var read = await Store.ReadRecordsAsync(Orders);

        Assert.Equal(["a", "b", "a"], read.Select(r => r.Id));
    }

    [Fact]
    public async Task Forgetting_a_record_removes_every_line_of_it()
    {
        await Store.AddRecordAsync(Orders, Rec("a", T0));
        await Store.AddRecordAsync(Orders, Rec("a", T0.AddHours(1)));
        await Store.AddRecordAsync(Orders, Rec("b", T0.AddMinutes(1)));

        Assert.True(await Store.ForgetAsync(Orders, "a"));

        Assert.Equal(["b"], (await Store.ReadRecordsAsync(Orders)).Select(r => r.Id));
        Assert.False(await Store.ForgetAsync(Orders, "a"));
    }

    [Fact]
    public async Task A_record_settled_again_since_the_cut_off_keeps_all_its_lines()
    {
        await Store.AddRecordAsync(Orders, Rec("kept", T0));                   // first settled before the cut-off …
        await Store.AddRecordAsync(Orders, Rec("kept", T0.AddDays(10)));       // … settled again after it
        await Store.AddRecordAsync(Orders, Rec("gone", T0));
        await Store.AddRecordAsync(Orders, Rec("gone", T0.AddDays(1)));        // every line of this one is older

        var gone = await Store.ForgetBeforeAsync(Orders, T0.AddDays(5));

        // The record goes once, however many lines it had, and takes all of them; the other loses nothing.
        Assert.Equal(["gone"], gone);
        var read = await Store.ReadRecordsAsync(Orders);
        Assert.Equal(["kept", "kept"], read.Select(r => r.Id));
        Assert.Equal([T0, T0.AddDays(10)], read.Select(r => r.SettledAt));
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
    /// <summary>The row's own key — a record settled more than once has one row for each time.</summary>
    public long Id { get; set; }
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
        // Not unique over the record id: a record settled again keeps its earlier rows (Lowline 0.12).
        modelBuilder.Entity<LowlineTestRecord>().HasIndex(r => new { r.Form, r.Space, r.RecordId });
        modelBuilder.Entity<LowlineTestTuning>().HasKey(t => new { t.Form, t.Space });
    }
}

/// <summary>What only the application-table store has to answer for: its record index, and the model it will run over.</summary>
public sealed class EfLowlineStoreTableTests : IDisposable
{
    private static readonly LowlineScope Orders = new("order", "plant-a");
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public EfLowlineStoreTableTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private EfLowlineStore<TDb, TRow, LowlineTestTuning> StoreOver<TDb, TRow>(out ServiceProvider provider)
        where TDb : DbContext
        where TRow : class, ILowlineRecordRow, new()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TDb>(o => o.UseSqlite(_connection));
        services.AddSingleton<EfLowlineStore<TDb, TRow, LowlineTestTuning>>();
        provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TDb>().Database.EnsureCreated();
        return provider.GetRequiredService<EfLowlineStore<TDb, TRow, LowlineTestTuning>>();
    }

    [Fact]
    public async Task The_record_index_gives_each_id_once_however_many_lines_it_has()
    {
        var store = StoreOver<LowlineTestDb, LowlineTestRecord>(out var provider);
        using var _ = provider;
        await store.AddRecordAsync(Orders, new SettledRecord("a", T0, "{}"));
        await store.AddRecordAsync(Orders, new SettledRecord("a", T0.AddHours(1), "{}"));
        await store.AddRecordAsync(Orders, new SettledRecord("b", T0.AddHours(2), "{}"));

        var ids = await ((ILowlineRecordIndex)store).ListRecordIdsAsync(Orders);

        Assert.Equal(["a", "b"], ids.OrderBy(i => i, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_table_that_is_unique_over_the_whole_record_identity_is_refused_with_the_change_to_make()
    {
        var store = StoreOver<UniqueByKeyDb, UniqueByKeyRecord>(out var provider);
        using var _ = provider;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AddRecordAsync(Orders, new SettledRecord("a", T0, "{}")));

        Assert.Contains("unique over (Form, Space, RecordId)", ex.Message);
        Assert.Contains("non-unique index", ex.Message);
    }

    [Fact]
    public async Task A_unique_index_over_the_record_identity_is_refused_too_and_on_a_read_as_well()
    {
        var store = StoreOver<UniqueByIndexDb, UniqueByIndexRecord>(out var provider);
        using var _ = provider;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadRecordsAsync(Orders));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListScopesAsync());
    }

    [Fact]
    public async Task A_key_that_also_holds_the_settlement_time_is_accepted()
    {
        // (form, space, record id, settled at) tells two settlements of one id apart — the shape a table that used to be
        // unique over the record id can move to without a new column.
        var store = StoreOver<TimedKeyDb, TimedKeyRecord>(out var provider);
        using var _ = provider;

        await store.AddRecordAsync(Orders, new SettledRecord("a", T0, "{}"));
        await store.AddRecordAsync(Orders, new SettledRecord("a", T0.AddHours(1), "{}"));

        Assert.Equal(2, (await store.ReadRecordsAsync(Orders)).Count);
    }
}

public sealed class UniqueByKeyRecord : ILowlineRecordRow
{
    public string Form { get; set; } = "";
    public string Space { get; set; } = "";
    public string RecordId { get; set; } = "";
    public DateTimeOffset SettledAt { get; set; }
    public string Json { get; set; } = "";
}

public sealed class UniqueByKeyDb(DbContextOptions<UniqueByKeyDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UniqueByKeyRecord>().HasKey(r => new { r.Form, r.Space, r.RecordId });
        modelBuilder.Entity<LowlineTestTuning>().HasKey(t => new { t.Form, t.Space });
    }
}

public sealed class UniqueByIndexRecord : ILowlineRecordRow
{
    public long Id { get; set; }
    public string Form { get; set; } = "";
    public string Space { get; set; } = "";
    public string RecordId { get; set; } = "";
    public DateTimeOffset SettledAt { get; set; }
    public string Json { get; set; } = "";
}

public sealed class UniqueByIndexDb(DbContextOptions<UniqueByIndexDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UniqueByIndexRecord>().HasIndex(r => new { r.Form, r.Space, r.RecordId }).IsUnique();
        modelBuilder.Entity<LowlineTestTuning>().HasKey(t => new { t.Form, t.Space });
    }
}

public sealed class TimedKeyRecord : ILowlineRecordRow
{
    public string Form { get; set; } = "";
    public string Space { get; set; } = "";
    public string RecordId { get; set; } = "";
    public DateTimeOffset SettledAt { get; set; }
    public string Json { get; set; } = "";
}

public sealed class TimedKeyDb(DbContextOptions<TimedKeyDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TimedKeyRecord>().HasKey(r => new { r.Form, r.Space, r.RecordId, r.SettledAt });
        modelBuilder.Entity<LowlineTestTuning>().HasKey(t => new { t.Form, t.Space });
    }
}
