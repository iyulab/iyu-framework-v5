using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// Controller-level smoke tests for <see cref="IyuODataController{TRead,TWrite}"/>.
/// These bypass OData URL routing and exercise the action methods directly to
/// cover the CRUD + Read↔Write copy logic introduced in C8. Full HTTP-level
/// OData routing is covered by the Yesung E2E test (C13).
/// </summary>
public class IyuODataControllerTests
{
    public sealed class BankAccount : IyuEntity
    {
        public string BankName { get; set; } = "";
        public string AccountNumber { get; set; } = "";
    }

    /// <summary>
    /// "Read" type — in real generated code this would be backed by a SQL view.
    /// In the test it shares the same table as <see cref="BankAccount"/> so that
    /// InMemory can satisfy both queries from the same store.
    /// </summary>
    public sealed class BankAccountExt : IyuEntity
    {
        public string BankName { get; set; } = "";
        public string AccountNumber { get; set; } = "";
        // Simulate a lookup field that exists only on the read side.
        public string? BankCountry { get; set; }
    }

    public sealed class TestContext(DbContextOptions<TestContext> options) : IyuDbContext(options)
    {
        public DbSet<BankAccount> BankAccounts => Set<BankAccount>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Map BankAccountExt onto the same InMemory store by using the same
            // table-ish name — InMemory ignores tables but EF needs the entity
            // in its model. A shadow entity pointing at a separate set suffices.
            modelBuilder.Entity<BankAccountExt>().HasKey(x => x.Id);
        }

        public DbSet<BankAccountExt> BankAccountsExt => Set<BankAccountExt>();
    }

    public sealed class BankAccountsController(TestContext ctx)
        : IyuODataController<BankAccountExt, BankAccount>(ctx);

    private static (TestContext ctx, BankAccountsController controller) CreateSut(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var ctx = new TestContext(options);
        var controller = new BankAccountsController(ctx)
        {
            ControllerContext = new()
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return (ctx, controller);
    }

    /// <summary>
    /// InMemory doesn't persist views, so we mirror writes to the read set so
    /// queries can find the row. In production this happens automatically via
    /// the underlying SQL view.
    /// </summary>
    private static async Task MirrorAsync(TestContext ctx, BankAccount write)
    {
        ctx.BankAccountsExt.Add(new BankAccountExt
        {
            Id = write.Id,
            BankName = write.BankName,
            AccountNumber = write.AccountNumber,
            BankCountry = "KR",
            CreatedAt = write.CreatedAt,
            UpdatedAt = write.UpdatedAt,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Post_creates_write_row_and_returns_read_projection()
    {
        var (ctx, controller) = CreateSut(nameof(Post_creates_write_row_and_returns_read_projection));

        var body = new BankAccountExt
        {
            BankName = "우리은행",
            AccountNumber = "1002-123-456789"
        };
        var result = await controller.Post(body, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result);
        // Persisted write row exists with timestamps populated by the interceptor.
        var persisted = await ctx.BankAccounts.SingleAsync();
        Assert.Equal("우리은행", persisted.BankName);
        Assert.NotEqual(default, persisted.CreatedAt);
        Assert.NotEqual(Guid.Empty, persisted.Id);
        Assert.NotNull(created.Value);
    }

    [Fact]
    public async Task Get_by_key_returns_404_when_missing()
    {
        var (_, controller) = CreateSut(nameof(Get_by_key_returns_404_when_missing));
        var result = await controller.Get(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Get_queryable_returns_read_set()
    {
        var (ctx, controller) = CreateSut(nameof(Get_queryable_returns_read_set));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "국민은행", AccountNumber = "123" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();
        await MirrorAsync(ctx, write);

        var queryable = controller.Get();
        var list = queryable.ToList();
        Assert.Single(list);
        Assert.Equal("국민은행", list[0].BankName);
    }

    [Fact]
    public async Task Patch_updates_existing_write_row()
    {
        var (ctx, controller) = CreateSut(nameof(Patch_updates_existing_write_row));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "하나은행", AccountNumber = "999" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();

        var delta = new Delta<BankAccountExt>();
        delta.TrySetPropertyValue(nameof(BankAccountExt.AccountNumber), "555-000");

        var result = await controller.Patch(write.Id, delta, CancellationToken.None);
        Assert.IsType<StatusCodeResult>(result);

        var reloaded = await ctx.BankAccounts.SingleAsync();
        Assert.Equal("555-000", reloaded.AccountNumber);
        Assert.Equal("하나은행", reloaded.BankName); // untouched
    }

    [Fact]
    public async Task Delete_removes_write_row()
    {
        var (ctx, controller) = CreateSut(nameof(Delete_removes_write_row));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "x", AccountNumber = "y" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();

        var result = await controller.Delete(write.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await ctx.BankAccounts.ToListAsync());
    }

    [Fact]
    public async Task Delete_returns_404_when_missing()
    {
        var (_, controller) = CreateSut(nameof(Delete_returns_404_when_missing));
        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Post_assigns_new_guid_when_body_id_is_empty()
    {
        var (ctx, controller) = CreateSut(nameof(Post_assigns_new_guid_when_body_id_is_empty));
        var body = new BankAccountExt { BankName = "a", AccountNumber = "b" };
        Assert.Equal(Guid.Empty, body.Id);
        await controller.Post(body, CancellationToken.None);
        var persisted = await ctx.BankAccounts.SingleAsync();
        Assert.NotEqual(Guid.Empty, persisted.Id);
    }
}
