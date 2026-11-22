using System.Net;
using System.Text.Json.Nodes;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Tests.Server.OData;
using Iyu.UBoard;
using Iyu.UBoard.MainServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Iyu.Tests.UBoard;

public enum CatalogStatus
{
    Paid,
    Pending,
    Refunded,
}

public sealed class CatalogCustomer : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class CatalogCustomerExt : IyuEntity
{
    public string Name { get; set; } = "";

    public string Email { get; set; } = "";

    public List<CatalogOrderExt> Orders { get; set; } = [];
}

public sealed class CatalogOrder : IyuEntity
{
    public decimal Total { get; set; }

    public Guid? CustomerId { get; set; }
}

public sealed class CatalogOrderExt : IyuEntity
{
    public decimal Total { get; set; }

    public decimal Margin { get; set; }

    public int? Quantity { get; set; }

    public CatalogStatus Status { get; set; }

    public Guid? CustomerId { get; set; }

    public CatalogCustomerExt? Customer { get; set; }
}

public sealed class CatalogAudit : IyuEntity
{
    public string Entry { get; set; } = "";
}

public sealed class CatalogAuditExt : IyuEntity
{
    public string Entry { get; set; } = "";
}

public sealed class CatalogContext(DbContextOptions<CatalogContext> options) : IyuDbContext(options)
{
    public DbSet<CatalogCustomer> Customers => Set<CatalogCustomer>();

    public DbSet<CatalogCustomerExt> CustomersExt => Set<CatalogCustomerExt>();

    public DbSet<CatalogOrder> Orders => Set<CatalogOrder>();

    public DbSet<CatalogOrderExt> OrdersExt => Set<CatalogOrderExt>();

    public DbSet<CatalogAudit> Audits => Set<CatalogAudit>();

    public DbSet<CatalogAuditExt> AuditsExt => Set<CatalogAuditExt>();
}

/// <summary>
/// The source catalog lists what a board can be built from — the host's OData sets, their properties and links — as the caller may
/// read them: the same read policies the OData surface enforces, not a second set of rules. An entity or property the caller may not
/// read is left out, unless the host opted in to seeing it marked, and then nothing about the policy itself is said.
/// </summary>
public sealed class MainServerSourceCatalogTests
{
    private const string Catalog = "/uboard/catalog";
    private const string MarginPolicy = "margin.read";
    private const string EmailPolicy = "email.read";
    private const string AuditPolicy = "audit.read";

    private static async Task<WebApplication> StartAsync(bool showUnreadable = false, string? fallbackClaim = null, bool register = true, bool withOrdersPolicy = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            foreach (var policy in new[] { MarginPolicy, EmailPolicy, AuditPolicy, "orders.read" })
                o.AddPolicy(policy, p => p.RequireClaim("perm", policy));
            if (fallbackClaim is not null) o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireClaim("perm", fallbackClaim).Build();
        });

        var dbName = "catalog-" + Guid.NewGuid().ToString("N");
        builder.Services.AddIyuMainServer<CatalogContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ODataModel.AddEntityPair<CatalogOrderExt, CatalogOrder>("Orders");
                options.ODataModel.AddEntityPair<CatalogCustomerExt, CatalogCustomer>("Customers");
                options.ODataModel.AddEntityPair<CatalogAuditExt, CatalogAudit>("Audits");
                options.AuthorizeProperty<CatalogOrderExt>(x => x.Margin, MarginPolicy);
                options.AuthorizeProperty<CatalogCustomerExt>(x => x.Email, EmailPolicy);
                options.Authorize<CatalogAuditExt>(read: AuditPolicy);
                if (withOrdersPolicy) options.Authorize<CatalogOrderExt>(read: "orders.read");
            });
        builder.Services.AddIyuUBoard(o => o.Catalog(showUnreadable));
        if (register) builder.Services.AddIyuUBoardMainServerCatalog();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Ask(params string[] perms)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Catalog);
        request.Headers.Add("X-Test-Perm", "signed-in"); // a caller the handler knows; the policies are the other claims
        foreach (var perm in perms) request.Headers.Add("X-Test-Perm", perm);
        return request;
    }

    private static async Task<JsonObject> Read(WebApplication app, params string[] perms)
    {
        using var response = await app.GetTestServer().CreateClient().SendAsync(Ask(perms));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static JsonObject Entity(JsonObject catalog, string name) =>
        (JsonObject)catalog["entities"]!.AsArray().Single(e => (string?)e!["name"] == name)!;

    private static JsonObject? Property(JsonObject entity, string name) =>
        entity["properties"]!.AsArray().SingleOrDefault(p => (string?)p!["name"] == name) as JsonObject;

    private static IEnumerable<string> Names(JsonObject catalog) => catalog["entities"]!.AsArray().Select(e => (string)e!["name"]!);

    [Fact]
    public async Task The_entity_sets_of_the_hosts_model_are_listed_by_name_for_a_caller_who_may_read_them_all()
    {
        await using var app = await StartAsync();

        var catalog = await Read(app, MarginPolicy, EmailPolicy, AuditPolicy);

        Assert.Equal(["Audits", "Customers", "Orders"], Names(catalog));
    }

    [Fact]
    public async Task A_property_is_listed_with_the_type_the_model_gives_it_and_whether_it_may_be_empty()
    {
        await using var app = await StartAsync();

        var orders = Entity(await Read(app, MarginPolicy), "Orders");

        Assert.Equal("Edm.Decimal", (string?)Property(orders, "Total")!["type"]);
        Assert.False((bool)Property(orders, "Total")!["nullable"]!);
        Assert.Equal("Edm.Int32", (string?)Property(orders, "Quantity")!["type"]);
        Assert.True((bool)Property(orders, "Quantity")!["nullable"]!);
        Assert.True((bool)Property(orders, "Total")!["readable"]!);
    }

    [Fact]
    public async Task An_enum_property_names_its_type_and_lists_the_values_a_query_may_use()
    {
        await using var app = await StartAsync();

        var status = Property(Entity(await Read(app), "Orders"), "Status")!;

        Assert.EndsWith("CatalogStatus", (string?)status["type"], StringComparison.Ordinal);
        Assert.Equal(["Paid", "Pending", "Refunded"], status["members"]!.AsArray().Select(m => (string)m!));
        Assert.Null(Property(Entity(await Read(app), "Orders"), "Total")!["members"]);
    }

    [Fact]
    public async Task Links_name_the_set_they_lead_to_and_whether_they_lead_to_many()
    {
        await using var app = await StartAsync();

        var catalog = await Read(app);
        var customer = Entity(catalog, "Orders")["links"]!.AsArray().Single()!;
        var orders = Entity(catalog, "Customers")["links"]!.AsArray().Single()!;

        Assert.Equal("Customer", (string?)customer["name"]);
        Assert.Equal("Customers", (string?)customer["target"]);
        Assert.False((bool)customer["collection"]!);
        Assert.Equal("Orders", (string?)orders["name"]);
        Assert.Equal("Orders", (string?)orders["target"]);
        Assert.True((bool)orders["collection"]!);
    }

    [Fact]
    public async Task A_property_the_caller_may_not_read_is_not_listed_while_the_rest_of_its_entity_is()
    {
        await using var app = await StartAsync();

        var orders = Entity(await Read(app), "Orders");

        Assert.Null(Property(orders, "Margin"));
        Assert.NotNull(Property(orders, "Total"));
        Assert.Null(Property(Entity(await Read(app), "Customers"), "Email"));
    }

    [Fact]
    public async Task The_caller_who_satisfies_the_property_policy_gets_the_property_and_the_catalog_differs_per_caller()
    {
        await using var app = await StartAsync();

        var finance = await Read(app, MarginPolicy);
        var analyst = await Read(app, EmailPolicy);

        Assert.True((bool)Property(Entity(finance, "Orders"), "Margin")!["readable"]!);
        Assert.Null(Property(Entity(finance, "Customers"), "Email"));
        Assert.Null(Property(Entity(analyst, "Orders"), "Margin"));
        Assert.True((bool)Property(Entity(analyst, "Customers"), "Email")!["readable"]!);
    }

    [Fact]
    public async Task An_entity_whose_read_policy_the_caller_fails_is_not_listed_and_no_link_leads_to_it()
    {
        await using var app = await StartAsync();

        var catalog = await Read(app);

        Assert.DoesNotContain("Audits", Names(catalog));
        Assert.Contains("Audits", Names(await Read(app, AuditPolicy)));
    }

    [Fact]
    public async Task A_link_to_a_set_the_caller_may_not_read_is_not_listed()
    {
        await using var app = await StartAsync(withOrdersPolicy: true);

        var catalog = await Read(app);

        Assert.DoesNotContain("Orders", Names(catalog));
        Assert.Empty(Entity(catalog, "Customers")["links"]!.AsArray());
    }

    [Fact]
    public async Task With_the_opt_in_what_the_caller_may_not_read_is_listed_and_marked_and_nothing_names_the_policy()
    {
        await using var app = await StartAsync(showUnreadable: true);
        using var response = await app.GetTestServer().CreateClient().SendAsync(Ask());
        var text = await response.Content.ReadAsStringAsync();
        var catalog = (JsonObject)JsonNode.Parse(text)!;

        Assert.False((bool)Property(Entity(catalog, "Orders"), "Margin")!["readable"]!);
        Assert.True((bool)Property(Entity(catalog, "Orders"), "Total")!["readable"]!);
        var audits = Entity(catalog, "Audits");
        Assert.False((bool)audits["readable"]!);
        Assert.All(audits["properties"]!.AsArray(), p => Assert.False((bool)p!["readable"]!));
        Assert.DoesNotContain(MarginPolicy, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AuditPolicy, text, StringComparison.Ordinal);
        Assert.DoesNotContain(EmailPolicy, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_opt_in_a_caller_who_may_read_everything_sees_everything_marked_readable()
    {
        await using var app = await StartAsync(showUnreadable: true);

        var catalog = await Read(app, MarginPolicy, EmailPolicy, AuditPolicy);

        Assert.All(catalog["entities"]!.AsArray(), e =>
        {
            Assert.True((bool)e!["readable"]!);
            Assert.All(e["properties"]!.AsArray(), p => Assert.True((bool)p!["readable"]!));
        });
    }

    [Fact]
    public async Task A_set_that_names_no_read_policy_is_held_to_the_hosts_fallback_policy()
    {
        await using var app = await StartAsync(fallbackClaim: "member");

        var outsider = await Read(app, MarginPolicy);
        var member = await Read(app, "member");

        Assert.Empty(Names(outsider));
        Assert.Equal(["Customers", "Orders"], Names(member));
    }

    [Fact]
    public async Task A_caller_who_is_not_signed_in_gets_401_and_a_request_from_another_site_403()
    {
        await using var app = await StartAsync();
        var client = app.GetTestServer().CreateClient();

        using var anonymous = await client.GetAsync(Catalog);
        using var crossSite = Ask(MarginPolicy);
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var foreign = await client.SendAsync(crossSite);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task The_catalog_is_not_a_route_unless_the_host_turned_it_on()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IBoardStore, NoBoards>();
        builder.Services.AddSingleton<IBoardAccess, EveryoneSignedInBoardAccess>();
        builder.Services.AddIyuUBoard(o => o.Boards()); // another part is on; the catalog is not
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();
        await using var _ = app;

        using var response = await app.GetTestServer().CreateClient().SendAsync(Ask(MarginPolicy));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_host_that_turns_the_catalog_on_without_registering_a_source_does_not_start()
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var app = await StartAsync(register: false);
        });

        Assert.Contains("IBoardSourceCatalog", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Registering_the_catalog_twice_is_refused()
    {
        var services = new ServiceCollection();
        services.AddIyuUBoardMainServerCatalog();

        Assert.Throws<InvalidOperationException>(() => services.AddIyuUBoardMainServerCatalog());
    }

    private sealed class NoBoards : IBoardStore
    {
        public Task<BoardChange> CreateAsync(string id, string name, string document, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BoardRecord?> FindAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BoardChange> SaveAsync(string id, string document, int expectedRevision, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BoardRevisionRecord?> FindRevisionAsync(string id, int revision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BoardRevisionRecord>> ListRevisionsAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BoardPublicationRecord?> PublishAsync(string id, int? revision, string publishedBy, DateTimeOffset at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BoardPublicationRecord>> ListPublicationsAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BoardSummary>> ListAsync(int skip, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BoardChange> RenameAsync(string id, string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
