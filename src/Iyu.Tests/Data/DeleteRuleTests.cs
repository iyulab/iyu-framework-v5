using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Data.WriteRules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Data;

public class DelDoc : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class DelOrder : DelDoc
{
    public List<DelLine> Lines { get; set; } = [];
}

public sealed class DelLine : IyuEntity
{
    public Guid OrderId { get; set; }
    public DelOrder? Order { get; set; }
    public string Label { get; set; } = "";
}

public sealed class DelTombstone : IyuEntity
{
    public Guid SourceId { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class DelContext(DbContextOptions<DelContext> options) : IyuDbContext(options)
{
    public DbSet<DelDoc> Docs => Set<DelDoc>();
    public DbSet<DelOrder> Orders => Set<DelOrder>();
    public DbSet<DelLine> Lines => Set<DelLine>();
    public DbSet<DelTombstone> Tombstones => Set<DelTombstone>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // One tombstone per source and kind — lets a test make the rule's own row fail the save.
        modelBuilder.Entity<DelTombstone>().HasIndex(t => new { t.SourceId, t.Kind }).IsUnique();
    }
}

/// <summary>Writes a tombstone through the rule's context — saved by the same SaveChanges as the delete.</summary>
public sealed class TombstoneOnOrderDelete : EntityDeleteRule<DelOrder>
{
    protected override void OnDeleting(WriteRuleContext<DelOrder> ctx)
        => ctx.Db.Add(new DelTombstone { SourceId = ctx.Entity.Id, Kind = "order", Name = ctx.Entity.Name });
}

public sealed class TombstoneOnLineDelete : EntityDeleteRule<DelLine>
{
    protected override void OnDeleting(WriteRuleContext<DelLine> ctx)
        => ctx.Db.Add(new DelTombstone { SourceId = ctx.Entity.Id, Kind = "line", Name = ctx.Entity.Label });
}

/// <summary>A rule on a base type: it must cover a derived entity's delete the way insert/update rules do.</summary>
public sealed class TombstoneOnDocDelete : EntityDeleteRule<DelDoc>
{
    protected override void OnDeleting(WriteRuleContext<DelDoc> ctx)
        => ctx.Db.Add(new DelTombstone { SourceId = ctx.Entity.Id, Kind = "doc", Name = ctx.Entity.Name });
}

/// <summary>Records exactly what a delete rule is told. Deliberately has no other effect.</summary>
public sealed class DeleteProbe : EntityDeleteRule<DelOrder>
{
    public List<(Guid Id, string Name, bool IsDeleted, bool IsAdded, bool IsUpdated, string? OriginalName)> Seen { get; } = [];

    protected override void OnDeleting(WriteRuleContext<DelOrder> ctx)
        => Seen.Add((ctx.Entity.Id, ctx.Entity.Name, ctx.IsDeleted, ctx.IsAdded, ctx.IsUpdated,
            ctx.Original<string>(nameof(DelOrder.Name))));
}

/// <summary>Counts what a value rule is dispatched for. It must not hear about deletes.</summary>
public sealed class ValueProbe : EntityWriteRule<DelOrder>
{
    public int Calls { get; private set; }
    protected override void Apply(WriteRuleContext<DelOrder> ctx) => Calls++;
}

public sealed class RefuseLockedOrderDelete : EntityDeleteRule<DelOrder>
{
    protected override void OnDeleting(WriteRuleContext<DelOrder> ctx)
    {
        if (ctx.Entity.Name == "locked") throw new DomainRuleException("a locked order cannot be deleted");
    }
}

/// <summary>
/// A rule written straight against the interface, the way the assembly-scan fixture does: it only
/// implements <c>Apply</c>. Adding the delete half to the interface must not break it.
/// </summary>
public sealed class DirectApplyOnlyRule : IEntityWriteRule
{
    public int Applied { get; private set; }
    public Type EntityType => typeof(DelOrder);
    public void Apply(EntityEntry entry, DbContext context) => Applied++;
}

/// <summary>
/// What the delete half of the rule pipeline promises: the same place every entry point converges,
/// the same registration, the same transaction — and the limits stated so an app does not discover
/// them by losing a row.
/// </summary>
public class DeleteRuleTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private ServiceProvider InMemory(params IEntityWriteRule[] rules)
    {
        var services = new ServiceCollection();
        var dbName = "delrules-" + Guid.NewGuid().ToString("N");
        foreach (var rule in rules) services.AddScoped(_ => rule);
        services.AddScoped<IyuWriteRuleInterceptor>();
        services.AddDbContext<DelContext>((sp, db) =>
        {
            db.UseInMemoryDatabase(dbName);
            db.AddInterceptors(sp.GetRequiredService<IyuWriteRuleInterceptor>());
        });
        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        return provider;
    }

    private ServiceProvider Sqlite(params IEntityWriteRule[] rules)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        _disposables.Add(connection);

        var services = new ServiceCollection();
        foreach (var rule in rules) services.AddScoped(_ => rule);
        services.AddScoped<IyuWriteRuleInterceptor>();
        services.AddDbContext<DelContext>((sp, db) =>
        {
            db.UseSqlite(connection);
            db.AddInterceptors(sp.GetRequiredService<IyuWriteRuleInterceptor>());
        });
        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<DelContext>().Database.EnsureCreated();
        return provider;
    }

    private static DelContext NewScope(ServiceProvider sp) => sp.CreateScope().ServiceProvider.GetRequiredService<DelContext>();

    [Fact]
    public void A_delete_rule_is_told_about_the_row_with_the_values_it_had()
    {
        var probe = new DeleteProbe();
        var sp = InMemory(probe);
        var db = NewScope(sp);
        db.Orders.Add(new DelOrder { Name = "first" });
        db.SaveChanges();
        probe.Seen.Clear();

        var order = db.Orders.Single();
        db.Orders.Remove(order);
        db.SaveChanges();

        var seen = Assert.Single(probe.Seen);
        Assert.Equal(order.Id, seen.Id);
        Assert.Equal("first", seen.Name);
        Assert.True(seen.IsDeleted);
        Assert.False(seen.IsAdded);
        Assert.False(seen.IsUpdated);
        Assert.Equal("first", seen.OriginalName);
    }

    [Fact]
    public void A_delete_rule_is_not_dispatched_for_an_insert_or_an_update()
    {
        var probe = new DeleteProbe();
        var sp = InMemory(probe);
        var db = NewScope(sp);

        db.Orders.Add(new DelOrder { Name = "a" });
        db.SaveChanges();
        db.Orders.Single().Name = "b";
        db.SaveChanges();

        Assert.Empty(probe.Seen);
    }

    [Fact]
    public void A_value_rule_still_hears_inserts_and_updates_but_not_the_delete()
    {
        var value = new ValueProbe();
        var sp = InMemory(value);
        var db = NewScope(sp);

        db.Orders.Add(new DelOrder { Name = "a" });
        db.SaveChanges();
        db.Orders.Single().Name = "b";
        db.SaveChanges();
        Assert.Equal(2, value.Calls);

        db.Orders.Remove(db.Orders.Single());
        db.SaveChanges();

        Assert.Equal(2, value.Calls);
    }

    [Fact]
    public void Rows_the_rule_adds_are_saved_by_the_same_save_as_the_delete()
    {
        var sp = InMemory(new TombstoneOnOrderDelete());
        var db = NewScope(sp);
        db.Orders.Add(new DelOrder { Name = "gone" });
        db.SaveChanges();
        var id = db.Orders.Single().Id;

        db.Orders.Remove(db.Orders.Single());
        db.SaveChanges();

        var check = NewScope(sp);
        Assert.Empty(check.Orders);
        var tombstone = Assert.Single(check.Tombstones);
        Assert.Equal(id, tombstone.SourceId);
        Assert.Equal("gone", tombstone.Name);
    }

    [Fact]
    public void A_delete_rule_can_refuse_the_delete_with_the_apps_own_exception()
    {
        var sp = InMemory(new RefuseLockedOrderDelete());
        var db = NewScope(sp);
        db.Orders.Add(new DelOrder { Name = "locked" });
        db.SaveChanges();

        db.Orders.Remove(db.Orders.Single());
        var ex = Assert.Throws<DomainRuleException>(() => db.SaveChanges());

        Assert.Equal("a locked order cannot be deleted", ex.Message);
        Assert.Single(NewScope(sp).Orders);
    }

    [Fact]
    public void A_rule_on_a_base_type_covers_a_derived_entitys_delete()
    {
        var sp = InMemory(new TombstoneOnDocDelete());
        var db = NewScope(sp);
        db.Orders.Add(new DelOrder { Name = "derived" });
        db.SaveChanges();

        db.Orders.Remove(db.Orders.Single());
        db.SaveChanges();

        Assert.Equal("doc", Assert.Single(NewScope(sp).Tombstones).Kind);
    }

    [Fact]
    public void Every_deleted_entry_in_one_save_is_dispatched()
    {
        var probe = new DeleteProbe();
        var sp = InMemory(probe);
        var db = NewScope(sp);
        db.Orders.AddRange(new DelOrder { Name = "1" }, new DelOrder { Name = "2" }, new DelOrder { Name = "3" });
        db.SaveChanges();
        probe.Seen.Clear();

        db.Orders.RemoveRange(db.Orders.ToList());
        db.SaveChanges();

        Assert.Equal(["1", "2", "3"], probe.Seen.Select(s => s.Name).Order().ToArray());
    }

    [Fact]
    public void Tracked_dependents_a_cascade_deletes_are_dispatched_too()
    {
        var sp = InMemory(new TombstoneOnOrderDelete(), new TombstoneOnLineDelete());
        var db = NewScope(sp);
        db.Orders.Add(new DelOrder { Name = "o", Lines = [new DelLine { Label = "l1" }, new DelLine { Label = "l2" }] });
        db.SaveChanges();

        var order = db.Orders.Include(o => o.Lines).Single();
        db.Orders.Remove(order);
        db.SaveChanges();

        var kinds = NewScope(sp).Tombstones.Select(t => t.Kind).ToList().Order().ToArray();
        Assert.Equal(["line", "line", "order"], kinds);
    }

    [Fact]
    public void A_rule_written_directly_against_the_interface_is_unaffected_by_the_delete_half()
    {
        var legacy = new DirectApplyOnlyRule();
        var sp = InMemory(legacy);
        var db = NewScope(sp);
        db.Orders.Add(new DelOrder { Name = "a" });
        db.SaveChanges();
        Assert.Equal(1, legacy.Applied);

        db.Orders.Remove(db.Orders.Single());
        db.SaveChanges();

        Assert.Equal(1, legacy.Applied);
    }

    [Fact]
    public void The_assembly_scan_registers_delete_rules_with_the_rest()
    {
        var services = new ServiceCollection();
        services.AddIyuWriteRules(typeof(DeleteRuleTests).Assembly);

        Assert.Contains(services, d => d.ServiceType == typeof(IEntityWriteRule)
                                       && d.ImplementationType == typeof(TombstoneOnOrderDelete));
    }

    /// <summary>
    /// Atomicity is the reason the rule runs inside the save: when the rule's own row cannot be
    /// written, the delete does not happen either — nothing is forgotten and the row is still there.
    /// </summary>
    [Fact]
    public void When_the_rules_own_row_fails_the_save_the_row_is_not_deleted()
    {
        var sp = Sqlite(new TombstoneOnOrderDelete());
        var db = NewScope(sp);
        var order = new DelOrder { Name = "kept" };
        db.Orders.Add(order);
        // A tombstone already exists for this source — the rule's row will violate the unique index.
        db.Tombstones.Add(new DelTombstone { SourceId = order.Id, Kind = "order", Name = "earlier" });
        db.SaveChanges();

        db.Orders.Remove(db.Orders.Single());
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());

        var check = NewScope(sp);
        Assert.Single(check.Orders);
        Assert.Equal("earlier", Assert.Single(check.Tombstones).Name);
    }

    /// <summary>
    /// The stated limit: a bulk delete never reaches the change tracker, so no rule hears about it.
    /// Pinned so the documentation cannot drift into promising more.
    /// </summary>
    [Fact]
    public void A_bulk_delete_bypasses_the_change_tracker_and_so_the_rules()
    {
        var sp = Sqlite(new TombstoneOnOrderDelete());
        var db = NewScope(sp);
        db.Orders.Add(new DelOrder { Name = "bulk" });
        db.SaveChanges();

        var deleted = db.Orders.ExecuteDelete();

        Assert.Equal(1, deleted);
        Assert.Empty(NewScope(sp).Tombstones);
    }
}
