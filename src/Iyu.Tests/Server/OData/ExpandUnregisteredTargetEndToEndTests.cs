using System.Net;
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

// Top-level public types for the reason ODataTestServerRoutingTests documents.

public sealed class HiddenLedger : IyuEntity
{
    public string Secret { get; set; } = "";
}

public sealed class HiddenLedgerExt : IyuEntity
{
    public string Secret { get; set; } = "";
}

public sealed class OpenInvoice : IyuEntity
{
    public string Title { get; set; } = "";
    public Guid? LedgerId { get; set; }
}

public sealed class OpenInvoiceExt : IyuEntity
{
    public string Title { get; set; } = "";
    public Guid? LedgerId { get; set; }
    public HiddenLedgerExt? Ledger { get; set; }
}

public sealed class HiddenTargetContext(DbContextOptions<HiddenTargetContext> options) : IyuDbContext(options)
{
    public DbSet<OpenInvoice> Invoices => Set<OpenInvoice>();
    public DbSet<OpenInvoiceExt> InvoicesExt => Set<OpenInvoiceExt>();
    public DbSet<HiddenLedger> Ledgers => Set<HiddenLedger>();
    public DbSet<HiddenLedgerExt> LedgersExt => Set<HiddenLedgerExt>();
}

public sealed class OpenInvoicesController(HiddenTargetContext ctx)
    : IyuODataController<OpenInvoiceExt, OpenInvoice>(ctx);

/// <summary>
/// A registered read type whose navigation points at a read type that is <em>not</em> registered —
/// a model the application keeps off its data API. The data behind it is not served by any set, so
/// no policy was ever declared for it; the question is whether <c>$expand</c> serves it anyway.
/// </summary>
public class ExpandUnregisteredTargetEndToEndTests
{
    private const string InvoicesSet = "OpenInvoices";
    private const string LedgerSecret = "kept-off-the-api";

    private static async Task<WebApplication> StartAsync()
    {
        var dbName = "hidden-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<HiddenTargetContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(OpenInvoicesController).Assembly);
                options.ODataModel.AddEntityPair<OpenInvoiceExt, OpenInvoice>(InvoicesSet);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<HiddenTargetContext>();
        var ledgerId = Guid.NewGuid();
        ctx.LedgersExt.Add(new HiddenLedgerExt { Id = ledgerId, Secret = LedgerSecret });
        ctx.InvoicesExt.Add(new OpenInvoiceExt { Id = Guid.NewGuid(), Title = "an invoice", LedgerId = ledgerId });
        await ctx.SaveChangesAsync();
        return app;
    }

    [Fact]
    public async Task Expand_does_not_serve_a_target_no_set_serves()
    {
        var app = await StartAsync();
        try
        {
            var client = app.GetTestServer().CreateClient();
            using var resp = await client.GetAsync($"/$data/{InvoicesSet}?$expand=Ledger");
            var body = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain(LedgerSecret, body, StringComparison.Ordinal);
            // Refused as a query naming something the API does not have — not served empty.
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            // The contrast: the set itself still answers, so the refusal is about the navigation.
            var plain = await client.GetStringAsync($"/$data/{InvoicesSet}");
            Assert.Contains("an invoice", plain, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Metadata_does_not_describe_a_target_no_set_serves()
    {
        var app = await StartAsync();
        try
        {
            var metadata = await app.GetTestServer().CreateClient().GetStringAsync("/$data/$metadata");
            Assert.DoesNotContain(nameof(HiddenLedgerExt), metadata, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }
}
