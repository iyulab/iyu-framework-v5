using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
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

// Top-level public types for the same reason ODataTestServerRoutingTests documents:
// a nested controller is not IsPublic and MVC's ControllerFeatureProvider skips it.

/// <summary>Write (table) side — what is stored.</summary>
public sealed class Waybill : IyuEntity
{
    public string Memo { get; set; } = "";
    public string Status { get; set; } = "";
    [Timestamp] public uint Version { get; set; }
}

/// <summary>Read (view) side — the stored columns plus two a view derives.</summary>
public sealed class WaybillExt : IyuEntity
{
    public string Memo { get; set; } = "";
    public string Status { get; set; } = "";
    public string? OwnerName { get; set; }
    public int LineCount { get; set; }
    [Timestamp] public uint Version { get; set; }
}

public sealed class WaybillContext(DbContextOptions<WaybillContext> options) : IyuDbContext(options)
{
    public DbSet<Waybill> Entries => Set<Waybill>();
    public DbSet<WaybillExt> EntriesExt => Set<WaybillExt>();
}

public sealed class WaybillsController(WaybillContext ctx)
    : IyuODataController<WaybillExt, Waybill>(ctx);

/// <summary>
/// <c>$metadata</c> and the update path answer "can this property be written?" the same way: a property carries
/// <c>Org.OData.Core.V1.Computed</c> exactly when a <c>PATCH</c> naming only it is refused as
/// <c>UnwritableProperty</c>.
/// </summary>
/// <remarks>
/// A client that reads the metadata to decide what to offer for editing — a generated form, a typed client, a
/// contract test — has no other standard place to learn it. The pair here has one property of every kind the write
/// path refuses (a derived column, a write-excluded one, the server's timestamps, the row's version) and one it
/// stores, so neither side of the equivalence is vacuous.
/// </remarks>
public class MetadataWritabilityEndToEndTests
{
    private const string Set = "Waybills";
    private static readonly XNamespace Edm = "http://docs.oasis-open.org/odata/ns/edm";

    private static async Task<WebApplication> StartAsync(Guid seededId)
    {
        var dbName = "waybill-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<WaybillContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(WaybillsController).Assembly);
                options.ODataModel.AddEntityPair<WaybillExt, Waybill>(Set);
                options.ODataModel.ExcludeFromWrite<WaybillExt>(x => x.Status);
            });

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WaybillContext>();
        db.Entries.Add(new Waybill { Id = seededId, Memo = "first", Status = "open", Version = 1 });
        await db.SaveChangesAsync();
        return app;
    }

    /// <summary>Each non-key property of the read type, with whether the metadata marks it computed and its EDM type.</summary>
    private static async Task<List<(string Name, string Type, bool Computed)>> DeclaredPropertiesAsync(HttpClient client)
    {
        var document = XDocument.Parse(await client.GetStringAsync("/$data/$metadata"));
        var entityType = document.Descendants(Edm + "EntityType")
            .Single(t => (string?)t.Attribute("Name") == nameof(WaybillExt));
        var keys = entityType.Descendants(Edm + "PropertyRef").Select(k => (string)k.Attribute("Name")!).ToHashSet();

        return entityType.Elements(Edm + "Property")
            .Where(p => !keys.Contains((string)p.Attribute("Name")!))
            .Select(p => (
                Name: (string)p.Attribute("Name")!,
                Type: (string)p.Attribute("Type")!,
                Computed: p.Elements(Edm + "Annotation").Any(a =>
                    (string?)a.Attribute("Term") == "Org.OData.Core.V1.Computed" && (string?)a.Attribute("Bool") == "true")))
            .ToList();
    }

    private static object SampleValue(string edmType) => edmType switch
    {
        "Edm.String" => "changed",
        "Edm.Int32" or "Edm.Int64" => 5,
        "Edm.DateTimeOffset" => "2030-01-01T00:00:00Z",
        _ => throw new InvalidOperationException($"No sample value for {edmType} — add one when the fixture grows."),
    };

    [Fact]
    public async Task The_metadata_marks_computed_exactly_the_properties_an_update_refuses()
    {
        var id = Guid.NewGuid();
        var app = await StartAsync(id);
        try
        {
            var client = app.GetTestServer().CreateClient();
            var properties = await DeclaredPropertiesAsync(client);

            foreach (var (name, type, computed) in properties)
            {
                using var response = await client.PatchAsync($"/$data/{Set}({id})",
                    JsonContent.Create(new Dictionary<string, object> { [name] = SampleValue(type) }));
                var body = await response.Content.ReadAsStringAsync();

                if (computed)
                {
                    Assert.True(response.StatusCode == HttpStatusCode.BadRequest && body.Contains(ODataErrorCodes.UnwritableProperty, StringComparison.Ordinal),
                        $"'{name}' is advertised as computed, but an update naming it answered {(int)response.StatusCode}: {body}");
                }
                else
                {
                    Assert.True(response.StatusCode == HttpStatusCode.NoContent,
                        $"'{name}' is not advertised as computed, but an update naming it answered {(int)response.StatusCode}: {body}");
                }
            }

            // Both sides of the equivalence are exercised, with one property of every refused kind.
            Assert.Equal(
                ["CreatedAt", "LineCount", "OwnerName", "Status", "UpdatedAt", "Version"],
                properties.Where(p => p.Computed).Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(["Memo"], properties.Where(p => !p.Computed).Select(p => p.Name));
        }
        finally { await app.DisposeAsync(); }
    }
}
