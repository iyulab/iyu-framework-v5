using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Data.WriteRules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Data;

public sealed class RuleOrder : IyuEntity
{
    public string VatType { get; set; } = "";
    public DateOnly? BilledDate { get; set; }
    public int Season { get; set; }
    public int Quantity { get; set; }
}

public sealed class RuleOrderLog : IyuEntity
{
    public Guid OrderId { get; set; }
    public int From { get; set; }
    public int To { get; set; }
}

public sealed class RuleContext(DbContextOptions<RuleContext> options) : IyuDbContext(options)
{
    public DbSet<RuleOrder> Orders => Set<RuleOrder>();
    public DbSet<RuleOrderLog> Logs => Set<RuleOrderLog>();
}

public sealed class DomainRuleException(string message) : Exception(message);

/// <summary>Group 1 — a conditional field lock.</summary>
public sealed class VatTypeLock : EntityWriteRule<RuleOrder>
{
    protected override void Apply(WriteRuleContext<RuleOrder> ctx)
    {
        if (!ctx.IsUpdated || !ctx.IsModified(nameof(RuleOrder.VatType))) return;
        if (ctx.Entity.BilledDate is not null)
            throw new DomainRuleException("vat type locked after billing");
    }
}

/// <summary>Group 2 — a default filled on insert. This is why OnModified alone is not enough.</summary>
public sealed class SeasonDefault : EntityWriteRule<RuleOrder>
{
    protected override void Apply(WriteRuleContext<RuleOrder> ctx)
    {
        if (ctx.IsAdded && ctx.Entity.Season == 0) ctx.Entity.Season = 2026;
    }
}

/// <summary>Group 3 — a change log, which needs the previous value and the context.</summary>
public sealed class QuantityLog : EntityWriteRule<RuleOrder>
{
    protected override void Apply(WriteRuleContext<RuleOrder> ctx)
    {
        if (!ctx.IsUpdated || !ctx.IsModified(nameof(RuleOrder.Quantity))) return;
        ctx.Db.Add(new RuleOrderLog
        {
            OrderId = ctx.Entity.Id,
            From = ctx.Original<int>(nameof(RuleOrder.Quantity)),
            To = ctx.Current<int>(nameof(RuleOrder.Quantity)),
        });
    }
}

/// <summary>Assigns a field, so a later rule in the same pass can be asked whether it sees it.</summary>
public sealed class AssignsVatType : EntityWriteRule<RuleOrder>
{
    protected override void Apply(WriteRuleContext<RuleOrder> ctx)
    {
        if (ctx.IsUpdated) ctx.Entity.VatType = "assigned";
    }
}

/// <summary>Records whether it observed the field as modified. Deliberately has no other effect.</summary>
public sealed class ObservesVatType : EntityWriteRule<RuleOrder>
{
    public bool? Saw { get; private set; }

    protected override void Apply(WriteRuleContext<RuleOrder> ctx)
    {
        if (ctx.IsUpdated) Saw = ctx.IsModified(nameof(RuleOrder.VatType));
    }
}

/// <summary>
/// The acceptance bar for this primitive was the three shapes a consumer reported writing 15
/// interceptors for — a conditional lock, a derivation that fires on insert, and a change log
/// needing the previous value. If any of the three cannot be expressed here, the primitive
/// has not replaced the thing it was meant to replace.
/// </summary>
public class WriteRuleTests
{
    private static ServiceProvider BuildProvider(params IEntityWriteRule[] rules)
    {
        var services = new ServiceCollection();
        var dbName = "rules-" + Guid.NewGuid().ToString("N");
        foreach (var rule in rules) services.AddScoped(_ => rule);
        services.AddScoped<IyuWriteRuleInterceptor>();
        services.AddDbContext<RuleContext>((sp, db) =>
        {
            db.UseInMemoryDatabase(dbName);
            db.AddInterceptors(sp.GetRequiredService<IyuWriteRuleInterceptor>());
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_lock_rejects_the_write_with_the_apps_own_exception()
    {
        using var sp = BuildProvider(new VatTypeLock());
        var db = sp.GetRequiredService<RuleContext>();

        db.Orders.Add(new RuleOrder { VatType = "a", BilledDate = new DateOnly(2026, 1, 1) });
        db.SaveChanges();

        var order = db.Orders.Single();
        order.VatType = "b";

        // The framework does not wrap it — the app keeps its own type and whatever maps it.
        var ex = Assert.Throws<DomainRuleException>(() => db.SaveChanges());
        Assert.Equal("vat type locked after billing", ex.Message);
    }

    [Fact]
    public void The_same_lock_allows_the_write_when_its_condition_is_false()
    {
        using var sp = BuildProvider(new VatTypeLock());
        var db = sp.GetRequiredService<RuleContext>();

        db.Orders.Add(new RuleOrder { VatType = "a", BilledDate = null });
        db.SaveChanges();

        db.Orders.Single().VatType = "b";
        db.SaveChanges();

        Assert.Equal("b", db.Orders.Single().VatType);
    }

    /// <summary>
    /// The correction that came out of designing this: a rule fired only on Modified cannot
    /// express a default, and defaults were the largest of the three groups.
    /// </summary>
    [Fact]
    public void A_derivation_fires_on_insert()
    {
        using var sp = BuildProvider(new SeasonDefault());
        var db = sp.GetRequiredService<RuleContext>();

        db.Orders.Add(new RuleOrder { VatType = "a" });
        db.SaveChanges();

        Assert.Equal(2026, db.Orders.Single().Season);
    }

    [Fact]
    public void A_change_log_reads_the_previous_value_and_writes_a_row_in_the_same_save()
    {
        using var sp = BuildProvider(new QuantityLog());
        var db = sp.GetRequiredService<RuleContext>();

        db.Orders.Add(new RuleOrder { VatType = "a", Quantity = 3 });
        db.SaveChanges();
        Assert.Empty(db.Logs);

        db.Orders.Single().Quantity = 7;
        db.SaveChanges();

        var log = Assert.Single(db.Logs);
        Assert.Equal(3, log.From);
        Assert.Equal(7, log.To);
    }

    [Fact]
    public void Rules_for_other_entity_types_are_not_dispatched()
    {
        using var sp = BuildProvider(new VatTypeLock());
        var db = sp.GetRequiredService<RuleContext>();

        // A log row is a different entity; the RuleOrder rule must not see it.
        db.Logs.Add(new RuleOrderLog { OrderId = Guid.NewGuid(), From = 1, To = 2 });
        db.SaveChanges();

        Assert.Single(db.Logs);
    }

    [Fact]
    public void With_no_rules_registered_saving_is_unaffected()
    {
        using var sp = BuildProvider();
        var db = sp.GetRequiredService<RuleContext>();

        db.Orders.Add(new RuleOrder { VatType = "a" });
        db.SaveChanges();

        Assert.Single(db.Orders);
    }

    [Fact]
    public void Assembly_scanning_finds_every_rule_and_does_not_double_register()
    {
        var services = new ServiceCollection();
        services.AddIyuWriteRules(typeof(WriteRuleTests).Assembly);
        services.AddIyuWriteRules(typeof(WriteRuleTests).Assembly);

        var registered = services
            .Where(d => d.ServiceType == typeof(IEntityWriteRule))
            .Select(d => d.ImplementationType)
            .ToList();

        Assert.Contains(typeof(VatTypeLock), registered);
        Assert.Contains(typeof(SeasonDefault), registered);
        Assert.Contains(typeof(QuantityLog), registered);
        Assert.Equal(registered.Count, registered.Distinct().Count());
    }

    [Fact]
    public async Task Rules_run_on_SaveChangesAsync_too()
    {
        using var sp = BuildProvider(new SeasonDefault());
        var db = sp.GetRequiredService<RuleContext>();

        db.Orders.Add(new RuleOrder { VatType = "a" });
        await db.SaveChangesAsync();

        Assert.Equal(2026, db.Orders.Single().Season);
    }

    /// <summary>
    /// 🔴 The one-pass guarantee covers *entries*, not *values*. A rule never sees a row another
    /// rule created — that part holds. But a field another rule **assigned** is read from the live
    /// entry at call time, so whether the second rule sees it depends on which ran first, and
    /// assembly scanning promises no order at all.
    /// <para>
    /// Measured, not reasoned about: registering the assigning rule first makes the field look
    /// modified; registering it second does not. Pinned in both directions so that a change to the
    /// dispatcher (snapshotting modified-state up front, say, which would make both `false`) shows
    /// up here as a deliberate decision rather than silently altering what a consumer's rule pair
    /// does. The README says as much where it describes the guarantee.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true, true)]    // assigning rule first  → the observer sees the assignment
    [InlineData(false, false)]  // assigning rule second → it does not
    public void Whether_a_rule_sees_a_field_another_rule_assigned_depends_on_registration_order(
        bool assignFirst, bool expectedSeen)
    {
        var assigns = new AssignsVatType();
        var observes = new ObservesVatType();
        using var sp = assignFirst
            ? BuildProvider(assigns, observes)
            : BuildProvider(observes, assigns);
        var db = sp.GetRequiredService<RuleContext>();

        db.Orders.Add(new RuleOrder { VatType = "a", Quantity = 1 });
        db.SaveChanges();

        var order = db.Orders.Single();
        order.Quantity = 2;              // a different field — VatType is untouched by the caller
        db.SaveChanges();

        Assert.True(observes.Saw.HasValue);
        Assert.Equal(expectedSeen, observes.Saw!.Value);
    }
}
