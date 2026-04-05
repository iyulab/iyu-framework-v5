using Iyu.Core.Entities;
using Iyu.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iyu.Tests.Data;

public class IyuTimestampInterceptorTests
{
    private sealed class Widget : IyuEntity
    {
        public string Name { get; set; } = "";
    }

    private sealed class TestContext(DbContextOptions<TestContext> options, TimeProvider clock)
        : IyuDbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
        public TimeProvider Clock { get; } = clock;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Skip base OnConfiguring to inject the fake clock; we add our own interceptor.
            optionsBuilder.AddInterceptors(new IyuTimestampInterceptor(Clock));
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TestContext CreateContext(FakeClock clock, string name)
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new TestContext(options, clock);
    }

    [Fact]
    public async Task Insert_sets_CreatedAt_and_UpdatedAt()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero));
        using var ctx = CreateContext(clock, nameof(Insert_sets_CreatedAt_and_UpdatedAt));

        var w = new Widget { Id = Guid.NewGuid(), Name = "alpha" };
        ctx.Widgets.Add(w);
        await ctx.SaveChangesAsync();

        Assert.Equal(clock.Now, w.CreatedAt);
        Assert.Equal(clock.Now, w.UpdatedAt);
    }

    [Fact]
    public async Task Update_refreshes_UpdatedAt_but_preserves_CreatedAt()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero));
        using var ctx = CreateContext(clock, nameof(Update_refreshes_UpdatedAt_but_preserves_CreatedAt));

        var w = new Widget { Id = Guid.NewGuid(), Name = "alpha" };
        ctx.Widgets.Add(w);
        await ctx.SaveChangesAsync();
        var createdAt = w.CreatedAt;

        clock.Now = clock.Now.AddHours(1);
        w.Name = "beta";
        // Attempt to smuggle in a manual CreatedAt change — interceptor must undo it.
        w.CreatedAt = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await ctx.SaveChangesAsync();

        Assert.Equal(clock.Now, w.UpdatedAt);
        // CreatedAt should not have been written to the store. Reload to verify.
        await ctx.Entry(w).ReloadAsync();
        Assert.Equal(createdAt, w.CreatedAt);
    }

    [Fact]
    public async Task Multiple_entities_in_same_save_share_one_timestamp()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero));
        using var ctx = CreateContext(clock, nameof(Multiple_entities_in_same_save_share_one_timestamp));

        var a = new Widget { Id = Guid.NewGuid(), Name = "a" };
        var b = new Widget { Id = Guid.NewGuid(), Name = "b" };
        ctx.Widgets.AddRange(a, b);
        await ctx.SaveChangesAsync();

        Assert.Equal(a.CreatedAt, b.CreatedAt);
        Assert.Equal(clock.Now, a.CreatedAt);
    }
}
