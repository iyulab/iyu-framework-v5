using Iyu.Core.Entities;
using Iyu.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Iyu.Tests.Data;

/// <summary>
/// <see cref="IyuDbContext"/> registers its two interceptors as <em>defaults</em>. EF keeps every
/// interceptor that was added and runs application interceptors in the order they were added, so
/// an unconditional append is not a no-op when the consumer already supplied the same type — the
/// later instance writes last and wins. These tests pin both halves: a supplied instance is left
/// alone, and a consumer who supplies nothing still gets the framework behaviour.
/// </summary>
public class IyuDbContextInterceptorDefaultsTests
{
    private sealed class Widget : IyuEntity
    {
        public string Name { get; set; } = "";
    }

    private sealed class PlainContext(DbContextOptions<PlainContext> options) : IyuDbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
    }

    /// <summary>
    /// Records what <c>base.OnConfiguring</c> left on the options, so the registration itself can be
    /// asserted rather than inferred from behaviour.
    /// </summary>
    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : IyuDbContext(options)
    {
        public List<IInterceptor> Registered { get; } = [];

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            Registered.AddRange(
                optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors ?? []);
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static List<IInterceptor> RegisteredOn(Action<DbContextOptionsBuilder<ProbeContext>> configure,
                                                   string name)
    {
        var builder = new DbContextOptionsBuilder<ProbeContext>().UseInMemoryDatabase(name);
        configure(builder);
        using var ctx = new ProbeContext(builder.Options);
        _ = ctx.Model; // forces OnConfiguring
        return ctx.Registered;
    }

    [Fact]
    public void A_consumer_supplied_timestamp_interceptor_is_not_joined_by_a_system_clock_one()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero));

        var registered = RegisteredOn(
            b => b.AddInterceptors(new IyuTimestampInterceptor(clock)),
            nameof(A_consumer_supplied_timestamp_interceptor_is_not_joined_by_a_system_clock_one));

        Assert.Single(registered.OfType<IyuTimestampInterceptor>());
        // The normalization interceptor was not supplied, so its default still arrives.
        Assert.Single(registered.OfType<IyuDateTimeOffsetNormalizationInterceptor>());
    }

    [Fact]
    public void A_consumer_supplied_normalization_interceptor_is_not_joined_by_a_second_one()
    {
        var registered = RegisteredOn(
            b => b.AddInterceptors(new IyuDateTimeOffsetNormalizationInterceptor()),
            nameof(A_consumer_supplied_normalization_interceptor_is_not_joined_by_a_second_one));

        Assert.Single(registered.OfType<IyuDateTimeOffsetNormalizationInterceptor>());
        Assert.Single(registered.OfType<IyuTimestampInterceptor>());
    }

    [Fact]
    public void Both_defaults_arrive_when_the_consumer_supplies_neither()
    {
        var registered = RegisteredOn(_ => { }, nameof(Both_defaults_arrive_when_the_consumer_supplies_neither));

        Assert.Single(registered.OfType<IyuTimestampInterceptor>());
        Assert.Single(registered.OfType<IyuDateTimeOffsetNormalizationInterceptor>());
    }

    /// <summary>
    /// The reported scenario, end to end: two rows that must look four minutes apart. Before the
    /// supplied-wins check the gap was zero, because the framework's system-clock instance ran last
    /// and overwrote what the supplied clock had written.
    /// </summary>
    [Fact]
    public async Task A_supplied_clock_decides_the_timestamps_a_save_writes()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<PlainContext>()
            .UseInMemoryDatabase(nameof(A_supplied_clock_decides_the_timestamps_a_save_writes))
            .AddInterceptors(new IyuTimestampInterceptor(clock))
            .Options;

        using var ctx = new PlainContext(options);

        var first = new Widget { Id = Guid.NewGuid(), Name = "first" };
        ctx.Widgets.Add(first);
        await ctx.SaveChangesAsync();

        clock.Now = clock.Now.AddMinutes(4);
        var second = new Widget { Id = Guid.NewGuid(), Name = "second" };
        ctx.Widgets.Add(second);
        await ctx.SaveChangesAsync();

        Assert.Equal(clock.Now.AddMinutes(-4), first.CreatedAt);
        Assert.Equal(clock.Now, second.CreatedAt);
        Assert.Equal(TimeSpan.FromMinutes(4), second.CreatedAt - first.CreatedAt);
    }
}
