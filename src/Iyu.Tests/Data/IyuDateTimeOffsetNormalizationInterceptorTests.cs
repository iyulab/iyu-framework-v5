using Iyu.Core.Entities;
using Iyu.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iyu.Tests.Data;

public class IyuDateTimeOffsetNormalizationInterceptorTests
{
    private sealed class Event : IyuEntity
    {
        public string Name { get; set; } = "";
        public DateTimeOffset OccurredAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
    }

    private sealed class TestContext(DbContextOptions<TestContext> options) : IyuDbContext(options)
    {
        public DbSet<Event> Events => Set<Event>();
    }

    private static TestContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new TestContext(options);
    }

    [Fact]
    public async Task Insert_normalizes_a_non_UTC_offset_to_zero_preserving_the_instant()
    {
        // The reported failure mode: a value bound with the server's local
        // offset (here +09:00, as an OData deserializer defaulting an
        // offset-less literal to local time would produce) instead of UTC.
        var local = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.FromHours(9));
        using var ctx = CreateContext(nameof(Insert_normalizes_a_non_UTC_offset_to_zero_preserving_the_instant));

        var e = new Event { Id = Guid.NewGuid(), Name = "alpha", OccurredAt = local };
        ctx.Events.Add(e);
        await ctx.SaveChangesAsync();

        Assert.Equal(TimeSpan.Zero, e.OccurredAt.Offset);
        // Same point in time, just re-expressed at offset zero — not shifted.
        Assert.Equal(local.ToUniversalTime(), e.OccurredAt);
        Assert.Equal(local, e.OccurredAt);
    }

    [Fact]
    public async Task Insert_leaves_an_already_UTC_value_untouched()
    {
        var utc = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
        using var ctx = CreateContext(nameof(Insert_leaves_an_already_UTC_value_untouched));

        var e = new Event { Id = Guid.NewGuid(), Name = "alpha", OccurredAt = utc };
        ctx.Events.Add(e);
        await ctx.SaveChangesAsync();

        Assert.Equal(utc, e.OccurredAt);
    }

    [Fact]
    public async Task Insert_normalizes_a_non_null_nullable_DateTimeOffset_and_leaves_null_alone()
    {
        var local = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.FromHours(9));
        using var ctx = CreateContext(nameof(Insert_normalizes_a_non_null_nullable_DateTimeOffset_and_leaves_null_alone));

        var e = new Event
        {
            Id = Guid.NewGuid(),
            Name = "alpha",
            OccurredAt = DateTimeOffset.UtcNow,
            ResolvedAt = local,
        };
        ctx.Events.Add(e);
        await ctx.SaveChangesAsync();

        Assert.NotNull(e.ResolvedAt);
        Assert.Equal(TimeSpan.Zero, e.ResolvedAt!.Value.Offset);
        Assert.Equal(local.ToUniversalTime(), e.ResolvedAt!.Value);

        var e2 = new Event { Id = Guid.NewGuid(), Name = "beta", OccurredAt = DateTimeOffset.UtcNow };
        ctx.Events.Add(e2);
        await ctx.SaveChangesAsync();

        Assert.Null(e2.ResolvedAt);
    }

    [Fact]
    public async Task Update_normalizes_a_newly_set_non_UTC_offset()
    {
        using var ctx = CreateContext(nameof(Update_normalizes_a_newly_set_non_UTC_offset));

        var e = new Event { Id = Guid.NewGuid(), Name = "alpha", OccurredAt = DateTimeOffset.UtcNow };
        ctx.Events.Add(e);
        await ctx.SaveChangesAsync();

        var local = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.FromHours(9));
        e.OccurredAt = local;
        await ctx.SaveChangesAsync();

        Assert.Equal(TimeSpan.Zero, e.OccurredAt.Offset);
        Assert.Equal(local.ToUniversalTime(), e.OccurredAt);
    }
}
