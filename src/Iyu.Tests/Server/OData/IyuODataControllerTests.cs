using System.ComponentModel.DataAnnotations;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>
    /// The annotations mirror what a generator emits onto the API-surface type:
    /// a required bounded string, an optional bounded one, and a display name
    /// that has to appear in the message a caller reads.
    /// </summary>
    public sealed class BankAccountExt : IyuEntity
    {
        [Required]
        [StringLength(30)]
        [Display(Name = "Bank name")]
        public string BankName { get; set; } = "";

        public string AccountNumber { get; set; } = "";

        // Simulate a lookup field that exists only on the read side.
        [StringLength(2)]
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

    /// <summary>
    /// The validation services a controller resolves at run time. Built from the
    /// real MVC registration rather than a hand-rolled validator, because the
    /// behaviour under test is precisely that create and partial update go
    /// through the <em>same</em> validator — a substitute here could agree with
    /// neither.
    /// </summary>
    private static readonly IServiceProvider ValidationServices = BuildValidationServices();

    private static IServiceProvider BuildValidationServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddDataAnnotations();
        return services.BuildServiceProvider();
    }

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
                // Without RequestServices the controller cannot resolve an
                // IObjectModelValidator, and TryValidateModel throws.
                HttpContext = new DefaultHttpContext { RequestServices = ValidationServices }
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

    /// <summary>
    /// Create's rejection — the side that already worked, and had no test.
    /// It is the reference the partial-update behaviour is measured against, so
    /// leaving it uncovered is how the two could drift apart unnoticed.
    /// </summary>
    [Fact]
    public async Task Post_rejects_a_body_that_violates_the_models_annotations()
    {
        var (ctx, controller) = CreateSut(nameof(Post_rejects_a_body_that_violates_the_models_annotations));

        var result = await PostAsBoundAsync(controller, new BankAccountExt { BankName = "", AccountNumber = "1" });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var state = Assert.IsType<SerializableError>(bad.Value);
        Assert.True(state.ContainsKey(nameof(BankAccountExt.BankName)));
        Assert.Empty(await ctx.BankAccounts.ToListAsync());
    }

    // ---------------------------------------------------------- partial-update validation
    //
    // The model's annotations used to hold on create and not on partial update,
    // so a value the model forbade could be written through the verb that did
    // not check. These pin both halves: what a partial update now rejects, and
    // what it must keep accepting.

    [Fact]
    public async Task Patch_rejects_a_sent_value_that_violates_the_models_annotations()
    {
        var (ctx, controller) = CreateSut(nameof(Patch_rejects_a_sent_value_that_violates_the_models_annotations));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "Acme Bank", AccountNumber = "999" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();

        var delta = new Delta<BankAccountExt>();
        delta.TrySetPropertyValue(nameof(BankAccountExt.BankName), "");

        var result = await controller.Patch(write.Id, delta, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var state = Assert.IsType<SerializableError>(bad.Value);
        Assert.True(state.ContainsKey(nameof(BankAccountExt.BankName)));

        // The stored value is untouched — a rejected request writes nothing.
        var reloaded = await ctx.BankAccounts.SingleAsync();
        Assert.Equal("Acme Bank", reloaded.BankName);
    }

    /// <summary>
    /// The message a caller reads has to come from the same place a create's
    /// does — including the display name. Building a second validator would
    /// produce a different sentence for the same violation.
    /// </summary>
    [Fact]
    public async Task Patch_and_post_report_the_same_violation_the_same_way()
    {
        var (ctx, controller) = CreateSut(nameof(Patch_and_post_report_the_same_violation_the_same_way));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "Acme Bank", AccountNumber = "999" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();

        var postErrors = ErrorsFor(
            await PostAsBoundAsync(controller, new BankAccountExt { BankName = "", AccountNumber = "1" }),
            nameof(BankAccountExt.BankName));

        var delta = new Delta<BankAccountExt>();
        delta.TrySetPropertyValue(nameof(BankAccountExt.BankName), "");
        var patchResult = await controller.Patch(write.Id, delta, CancellationToken.None);
        var patchErrors = ErrorsFor(patchResult, nameof(BankAccountExt.BankName));

        Assert.Equal(postErrors, patchErrors);
        Assert.Contains(patchErrors, m => m.Contains("Bank name", StringComparison.Ordinal));
    }

    /// <summary>
    /// Invokes create the way a request does. Calling the action directly skips
    /// model binding, and binding is what fills model state from the body's
    /// annotations — so a direct call reaches <c>Post</c> with empty, and
    /// therefore valid, model state no matter what the body contains.
    /// </summary>
    /// <remarks>
    /// This is a property of the harness, not of the server: over HTTP, create
    /// does reject the same body. It is also why create's rejection had no test
    /// — writing one looks like it should work and silently asserts nothing.
    /// Validating here is what binding would have done, and nothing more.
    /// </remarks>
    private static Task<IActionResult> PostAsBoundAsync(BankAccountsController controller, BankAccountExt body)
    {
        controller.TryValidateModel(body);
        return controller.Post(body, CancellationToken.None);
    }

    private static string[] ErrorsFor(IActionResult result, string key)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var state = Assert.IsType<SerializableError>(bad.Value);
        return (string[])state[key];
    }

    /// <summary>
    /// The half that must not break. Required fields the request never carried
    /// are not what a partial update is judged on — if they were, no entity with
    /// a required field could be patched at all.
    /// </summary>
    [Fact]
    public async Task Patch_still_accepts_an_update_that_omits_a_required_field()
    {
        var (ctx, controller) = CreateSut(nameof(Patch_still_accepts_an_update_that_omits_a_required_field));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "Acme Bank", AccountNumber = "999" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();

        var delta = new Delta<BankAccountExt>();
        delta.TrySetPropertyValue(nameof(BankAccountExt.AccountNumber), "555-000");

        var result = await controller.Patch(write.Id, delta, CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, Assert.IsType<StatusCodeResult>(result).StatusCode);
        var reloaded = await ctx.BankAccounts.SingleAsync();
        Assert.Equal("555-000", reloaded.AccountNumber);
        Assert.Equal("Acme Bank", reloaded.BankName);
    }

    /// <summary>
    /// An unknown key is answered before the payload is read, so an invalid
    /// payload for a row that does not exist is a 404. Pinned so that the order
    /// of two statements cannot quietly become the contract.
    /// </summary>
    [Fact]
    public async Task Patch_answers_an_unknown_key_before_it_judges_the_payload()
    {
        var (_, controller) = CreateSut(nameof(Patch_answers_an_unknown_key_before_it_judges_the_payload));

        var delta = new Delta<BankAccountExt>();
        delta.TrySetPropertyValue(nameof(BankAccountExt.BankName), "");

        var result = await controller.Patch(Guid.NewGuid(), delta, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// A consumer that fills a value in an override before calling the base
    /// implementation gets that value validated, not the placeholder it
    /// replaced. This is the extension point that makes a server-assigned field
    /// workable without a new hook API.
    /// </summary>
    [Fact]
    public async Task Patch_validates_the_delta_as_an_override_left_it()
    {
        var (ctx, controller) = CreateSut(nameof(Patch_validates_the_delta_as_an_override_left_it));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "Acme Bank", AccountNumber = "999" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();

        var delta = new Delta<BankAccountExt>();
        delta.TrySetPropertyValue(nameof(BankAccountExt.BankName), "");
        // What an override would do before delegating: replace the placeholder.
        delta.TrySetPropertyValue(nameof(BankAccountExt.BankName), "Assigned Bank");

        var result = await controller.Patch(write.Id, delta, CancellationToken.None);

        Assert.IsType<StatusCodeResult>(result);
        Assert.Equal("Assigned Bank", (await ctx.BankAccounts.SingleAsync()).BankName);
    }

    // ---------------------------------------------------------- recorded gaps
    //
    // Behaviour that is defensible but weak, pinned so that changing it is a
    // decision somebody makes rather than a side effect of unrelated work. The
    // reasoning lives on CopySelectedProperties; these assert the state.

    /// <summary>
    /// A property that exists only on the read side has no writable counterpart,
    /// so an update carrying only such properties changes nothing — and is still
    /// answered 204. Skipping is what lets a client send back an object it read;
    /// answering 204 to a request that could not have any effect is the part
    /// that is weak.
    /// </summary>
    [Fact]
    public async Task Patch_of_a_read_only_property_changes_nothing_and_still_reports_success()
    {
        var (ctx, controller) = CreateSut(nameof(Patch_of_a_read_only_property_changes_nothing_and_still_reports_success));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "Acme Bank", AccountNumber = "999" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();

        // BankCountry is on the read type only — the view produces it.
        var delta = new Delta<BankAccountExt>();
        delta.TrySetPropertyValue(nameof(BankAccountExt.BankCountry), "JP");

        var result = await controller.Patch(write.Id, delta, CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, Assert.IsType<StatusCodeResult>(result).StatusCode);

        var reloaded = await ctx.BankAccounts.SingleAsync();
        Assert.Equal("Acme Bank", reloaded.BankName);
        Assert.Equal("999", reloaded.AccountNumber);
    }

    /// <summary>
    /// The case the skipping exists for: an object read and sent back whole,
    /// derived properties included, still applies the field that changed.
    /// Whatever is decided about the case above must keep this working.
    /// </summary>
    [Fact]
    public async Task Patch_applies_a_writable_property_even_when_read_only_ones_travel_with_it()
    {
        var (ctx, controller) = CreateSut(nameof(Patch_applies_a_writable_property_even_when_read_only_ones_travel_with_it));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "Acme Bank", AccountNumber = "999" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();

        var delta = new Delta<BankAccountExt>();
        delta.TrySetPropertyValue(nameof(BankAccountExt.BankCountry), "JP");
        delta.TrySetPropertyValue(nameof(BankAccountExt.AccountNumber), "555-000");

        var result = await controller.Patch(write.Id, delta, CancellationToken.None);

        Assert.IsType<StatusCodeResult>(result);
        Assert.Equal("555-000", (await ctx.BankAccounts.SingleAsync()).AccountNumber);
    }

    /// <summary>
    /// Validation reaches read-only properties too: they are part of the API
    /// surface a caller sends, so a value that violates their annotations is
    /// rejected before the question of whether anything is writable arises.
    /// </summary>
    [Fact]
    public async Task Patch_validates_a_read_only_property_it_will_not_store()
    {
        var (ctx, controller) = CreateSut(nameof(Patch_validates_a_read_only_property_it_will_not_store));
        var write = new BankAccount { Id = Guid.NewGuid(), BankName = "Acme Bank", AccountNumber = "999" };
        ctx.BankAccounts.Add(write);
        await ctx.SaveChangesAsync();

        var delta = new Delta<BankAccountExt>();
        delta.TrySetPropertyValue(nameof(BankAccountExt.BankCountry), "too long for two");

        var result = await controller.Patch(write.Id, delta, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(Assert.IsType<SerializableError>(bad.Value).ContainsKey(nameof(BankAccountExt.BankCountry)));
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
