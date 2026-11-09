using System.Net;
using System.Net.Http.Json;
using Iyu.Core.Identity;
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

// Top-level public types for the same reason ExcludedPropertyEndToEndTests documents.

/// <summary>Write (table) side of the pair: an application's machine credential, deriving the framework's base.</summary>
public sealed class ShopServiceClient : IyuServiceClient
{
}

/// <summary>Read (view) side, a distinct class as in a real deployment.</summary>
public sealed class ShopServiceClientExt : IyuServiceClient
{
}

public sealed class ShopServiceClientContext(DbContextOptions<ShopServiceClientContext> options) : IyuDbContext(options)
{
    public DbSet<ShopServiceClient> Clients => Set<ShopServiceClient>();
    public DbSet<ShopServiceClientExt> ClientsExt => Set<ShopServiceClientExt>();
}

public sealed class ShopServiceClientsController(ShopServiceClientContext ctx)
    : IyuODataController<ShopServiceClientExt, ShopServiceClient>(ctx);

/// <summary>
/// A service client's <c>SecretHash</c> is withheld from the OData surface without the host having to know to exclude it
/// — a host that registers an entity derived from <see cref="IyuServiceClient"/> and does nothing else must not be handing
/// every caller what lets them test guesses at a credential offline.
/// </summary>
public class ServiceClientSecretEndToEndTests
{
    private const string Set = "ShopServiceClients";
    private const string Sentinel = "SENTINEL-CLIENT-HASH-MUST-NOT-LEAK";

    private enum Declaration { Nothing, ExposedOnPurpose, ExcludedByHost }

    private static async Task<WebApplication> StartAsync(Declaration declaration)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var dbName = "shop-clients-" + Guid.NewGuid().ToString("N");
        builder.Services.AddIyuMainServer<ShopServiceClientContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ShopServiceClientsController).Assembly);
                options.ODataModel.AddEntityPair<ShopServiceClientExt, ShopServiceClient>(Set);
                if (declaration == Declaration.ExposedOnPurpose)
                    options.ODataModel.Expose<ShopServiceClientExt>(x => x.SecretHash);
                if (declaration == Declaration.ExcludedByHost)
                    options.ODataModel.Exclude<ShopServiceClientExt>(x => x.SecretHash);   // what hosts wrote before the default existed
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<ShopServiceClientContext>();
        db2.ClientsExt.Add(new ShopServiceClientExt
        {
            Id = Guid.NewGuid(), ClientId = "billing-sync", SecretHash = Sentinel, DisplayName = "Billing sync",
        });
        await db2.SaveChangesAsync();
        return app;
    }

    [Theory]
    [InlineData("$select=SecretHash")]
    [InlineData("$filter=startswith(SecretHash,'S')")]    // probing a character at a time
    [InlineData("$orderby=SecretHash")]
    public async Task By_default_the_hash_cannot_be_selected_filtered_or_sorted_on(string query)
    {
        var app = await StartAsync(Declaration.Nothing);
        try
        {
            using var response = await app.GetTestServer().CreateClient().GetAsync($"/$data/{Set}?{query}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task By_default_the_plain_read_returns_the_client_without_its_hash()
    {
        var app = await StartAsync(Declaration.Nothing);
        try
        {
            using var response = await app.GetTestServer().CreateClient().GetAsync($"/$data/{Set}");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("billing-sync", body, StringComparison.Ordinal);       // the rest of the entity is served
            Assert.DoesNotContain(Sentinel, body, StringComparison.Ordinal);
            Assert.DoesNotContain("SecretHash", body, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task By_default_a_body_that_names_the_hash_is_refused_and_stores_nothing()
    {
        var app = await StartAsync(Declaration.Nothing);
        try
        {
            var id = Guid.NewGuid();
            using var response = await app.GetTestServer().CreateClient().PostAsJsonAsync($"/$data/{Set}",
                new { Id = id, ClientId = "x", DisplayName = "x", SecretHash = "PLANTED" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var scope = app.Services.CreateScope();
            Assert.Null(scope.ServiceProvider.GetRequiredService<ShopServiceClientContext>()
                .Clients.AsNoTracking().FirstOrDefault(c => c.Id == id));
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The host that writes down that it wants the hash gets it — the default is a default, not a wall.</summary>
    [Fact]
    public async Task A_host_that_exposes_it_on_purpose_serves_it()
    {
        var app = await StartAsync(Declaration.ExposedOnPurpose);
        try
        {
            using var response = await app.GetTestServer().CreateClient().GetAsync($"/$data/{Set}?$select=SecretHash");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(Sentinel, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>A host that already excluded the hash by hand keeps working: the default adds to its exclusion, it does not collide with it.</summary>
    [Fact]
    public async Task A_host_that_already_excluded_it_by_hand_is_not_refused_and_still_does_not_serve_it()
    {
        var app = await StartAsync(Declaration.ExcludedByHost);
        try
        {
            using var response = await app.GetTestServer().CreateClient().GetAsync($"/$data/{Set}?$select=SecretHash");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }
}
