using System.Net;
using System.Net.Http.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types, and named for the sets they serve: OData routes a request to a
// controller by matching the controller's name to the entity set name, and every controller in
// this assembly is discovered by every test host in it. Two fixtures reusing one set name would
// therefore route into each other's host and fail for a reason that has nothing to do with what
// they measure — so each fixture's names are its own.
//
// Machines own their keys. A ServicePlan's key is a Machine's key: the pair is one row of extra
// facts about one machine, not a collection of them. Memos are an ordinary set, present only as
// the control.

public sealed class Machine : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class MachineExt : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class ServicePlan : IyuEntity
{
    public string Interval { get; set; } = "";
}

public sealed class ServicePlanExt : IyuEntity
{
    public string Interval { get; set; } = "";
}

public sealed class Memo : IyuEntity
{
    public string Text { get; set; } = "";
}

public sealed class MemoExt : IyuEntity
{
    public string Text { get; set; } = "";
}

public sealed class ContractContext(DbContextOptions<ContractContext> options) : IyuDbContext(options)
{
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<MachineExt> MachinesExt => Set<MachineExt>();
    public DbSet<ServicePlan> ServicePlans => Set<ServicePlan>();
    public DbSet<ServicePlanExt> ServicePlansExt => Set<ServicePlanExt>();
    public DbSet<Memo> Memos => Set<Memo>();
    public DbSet<MemoExt> MemosExt => Set<MemoExt>();
}

public sealed class MachinesController(ContractContext ctx)
    : IyuODataController<MachineExt, Machine>(ctx);

public sealed class ServicePlansController(ContractContext ctx)
    : IyuODataController<ServicePlanExt, ServicePlan>(ctx);

public sealed class MemosController(ContractContext ctx)
    : IyuODataController<MemoExt, Memo>(ctx);

/// <summary>
/// What the generic write path does for an entity set whose key is not its own.
/// </summary>
/// <remarks>
/// <para>
/// A set declared through <c>DeclareSharedKey</c> holds rows whose key is also their reference to
/// a row of another set. Three requests an ordinary set accepts are wrong for this shape, and each
/// is refused with the status that describes it: a body with no key (the server must not choose
/// one), a key naming a principal that does not exist, and a key whose row is already there.
/// </para>
/// <para>
/// The in-memory provider enforces no foreign keys, so nothing here demonstrates the constraint
/// layer a relational provider would also apply. That is deliberate — these assertions are about
/// the answer a caller receives, not about which layer produces it.
/// </para>
/// </remarks>
public class SharedKeyWriteContractEndToEndTests
{
    private const string Principals = "Machines";
    private const string Dependents = "ServicePlans";
    private const string Plain = "Memos";

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "contract-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddIyuMainServer<ContractContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(MachinesController).Assembly);
                options.ODataModel.AddEntityPair<MachineExt, Machine>(Principals);
                options.ODataModel.AddEntityPair<ServicePlanExt, ServicePlan>(Dependents);
                options.ODataModel.AddEntityPair<MemoExt, Memo>(Plain);
                options.ODataModel.DeclareSharedKey(Dependents, Principals);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    private static async Task<Guid> SeedPrincipalAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ContractContext>();
        var id = Guid.NewGuid();
        ctx.Machines.Add(new Machine { Id = id, Name = "pump" });
        ctx.MachinesExt.Add(new MachineExt { Id = id, Name = "pump" });
        await ctx.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// The path that must keep working. Written first because every assertion below is a refusal,
    /// and a contract made only of refusals is satisfied by refusing everything.
    /// </summary>
    [Fact]
    public async Task A_post_naming_an_existing_principal_is_created()
    {
        await using var app = await StartAsync();
        var key = await SeedPrincipalAsync(app);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Dependents}", new { Id = key, Interval = "P30D" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// A body with no key is refused rather than given one.
    /// </summary>
    [Fact]
    public async Task A_post_with_no_key_is_refused()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Dependents}", new { Interval = "P30D" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cannot be assigned by the server", body, StringComparison.Ordinal);

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ContractContext>();
        Assert.Empty(ctx.ServicePlans);
    }

    /// <summary>
    /// A key naming no principal is a conflict with the current state, not a malformed request.
    /// </summary>
    [Fact]
    public async Task A_post_naming_a_principal_that_does_not_exist_is_refused()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Dependents}", new { Id = Guid.NewGuid(), Interval = "P30D" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains($"No row of entity set '{Principals}'", body, StringComparison.Ordinal);

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ContractContext>();
        Assert.Empty(ctx.ServicePlans);
    }

    /// <summary>
    /// A second row for the same principal is a conflict too — and answered as one rather than as
    /// whatever a key collision surfaces as further down.
    /// </summary>
    [Fact]
    public async Task A_second_post_for_the_same_principal_is_refused()
    {
        await using var app = await StartAsync();
        var key = await SeedPrincipalAsync(app);
        using var client = app.GetTestClient();

        using var first = await client.PostAsJsonAsync(
            $"/$data/{Dependents}", new { Id = key, Interval = "P30D" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await client.PostAsJsonAsync(
            $"/$data/{Dependents}", new { Id = key, Interval = "P7D" });
        var body = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("holds at most one row per principal", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The key stays the principal's: a PATCH cannot re-point an existing row at another one.
    /// </summary>
    /// <remarks>
    /// The generic PATCH already treats <c>Id</c> as server-managed for every set, so this asserts
    /// an existing guarantee rather than a new one. It is pinned here because for this shape the
    /// consequence differs in kind — a changed key would not edit a row, it would move it to a
    /// different owner — and a guarantee this contract leans on should fail visibly if it is ever
    /// relaxed elsewhere.
    /// </remarks>
    [Fact]
    public async Task A_patch_cannot_move_a_row_to_another_principal()
    {
        await using var app = await StartAsync();
        var key = await SeedPrincipalAsync(app);
        using var client = app.GetTestClient();

        using var created = await client.PostAsJsonAsync(
            $"/$data/{Dependents}", new { Id = key, Interval = "P30D" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var response = await client.PatchAsJsonAsync(
            $"/$data/{Dependents}({key})", new { Id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The negative control: a set that was never declared shared-key still has a key invented for
    /// it, which is the behaviour every ordinary set has always had.
    /// </summary>
    [Fact]
    public async Task A_set_that_shares_no_key_still_has_one_invented_for_it()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Plain}", new { Text = "note" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ContractContext>();
        var written = Assert.Single(ctx.Memos);
        Assert.NotEqual(Guid.Empty, written.Id);
    }

    /// <summary>
    /// And the same control on the other refusal: an ordinary set accepts a key that names nothing,
    /// because for it the key names nothing by design.
    /// </summary>
    [Fact]
    public async Task A_set_that_shares_no_key_accepts_any_key_the_caller_supplies()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/$data/{Plain}", new { Id = Guid.NewGuid(), Text = "note" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
