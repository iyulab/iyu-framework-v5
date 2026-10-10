using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types for the same reason ODataTestServerRoutingTests documents:
// a nested controller is not IsPublic and MVC's ControllerFeatureProvider skips it.

/// <summary>A model that is only read — a key and what a view would project. No audit timestamps, no write type.</summary>
public sealed class FreightSummary : IKeyed
{
    public Guid Id { get; set; }
    public string Carrier { get; set; } = "";
    public int Parcels { get; set; }
}

public sealed class FreightSummaryContext(DbContextOptions<FreightSummaryContext> options) : IyuDbContext(options)
{
    public DbSet<FreightSummary> Summaries => Set<FreightSummary>();
}

public sealed class FreightSummariesController(FreightSummaryContext ctx)
    : IyuODataReadOnlyController<FreightSummary>(ctx);

/// <summary>
/// A set registered with <see cref="IyuEdmModelBuilder.AddReadOnlySet{TRead}"/>: a read type with no write type
/// behind it, served like any set for reads and refusing every write.
/// </summary>
public class ReadOnlySetEndToEndTests
{
    private const string Set = "FreightSummaries";
    private static readonly XNamespace Edm = "http://docs.oasis-open.org/odata/ns/edm";
    private static readonly Guid Known = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private const string ReadPolicy = "freight.read";

    private static async Task<WebApplication> StartAsync(bool withReadPolicy = false)
    {
        var dbName = "freight-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (withReadPolicy)
        {
            builder.Services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
            builder.Services.AddAuthorization(opts => opts.AddPolicy(ReadPolicy, p => p.RequireClaim("perm", ReadPolicy)));
        }
        builder.Services.AddIyuMainServer<FreightSummaryContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(FreightSummariesController).Assembly);
                options.ODataModel.AddReadOnlySet<FreightSummary>(Set);
                if (withReadPolicy) options.Authorize<FreightSummary>(read: ReadPolicy);
            });

        var app = builder.Build();
        if (withReadPolicy) app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();

        // Rows arrive the way a view's would: something else wrote the data this model reads.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FreightSummaryContext>();
        db.Summaries.AddRange(
            new FreightSummary { Id = Known, Carrier = "north", Parcels = 3 },
            new FreightSummary { Id = Guid.NewGuid(), Carrier = "south", Parcels = 12 });
        await db.SaveChangesAsync();
        return app;
    }

    [Fact]
    public async Task It_is_read_like_any_set()
    {
        var app = await StartAsync();
        try
        {
            var client = app.GetTestServer().CreateClient();

            using var list = await client.GetAsync($"/$data/{Set}?$filter=Parcels gt 5&$select=Carrier");
            var listBody = await list.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Contains("south", listBody, StringComparison.Ordinal);
            Assert.DoesNotContain("north", listBody, StringComparison.Ordinal);

            using var one = await client.GetAsync($"/$data/{Set}({Known})");
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);
            Assert.Equal("north", JsonDocument.Parse(await one.Content.ReadAsStringAsync()).RootElement.GetProperty("Carrier").GetString());

            using var missing = await client.GetAsync($"/$data/{Set}({Guid.NewGuid()})");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The entity's read policy is declared and enforced exactly as for a pair.</summary>
    [Fact]
    public async Task Its_read_policy_is_the_entitys_like_any_set()
    {
        var app = await StartAsync(withReadPolicy: true);
        try
        {
            var client = app.GetTestServer().CreateClient();

            using var without = await client.GetAsync($"/$data/{Set}");
            Assert.True(without.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, without.StatusCode.ToString());

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/$data/{Set}({Known})");
            request.Headers.Add("X-Test-Perm", ReadPolicy);
            using var with = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, with.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>The same refusal a read-only pair gives, so a client handles one answer for "not written through here".</summary>
    [Theory]
    [InlineData("POST", false)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    public async Task Every_write_verb_is_refused_as_a_read_only_set(string method, bool keyed)
    {
        var app = await StartAsync();
        try
        {
            var path = keyed ? $"/$data/{Set}({Known})" : $"/$data/{Set}";
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "DELETE") request.Content = JsonContent.Create(new { Carrier = "planted" });

            using var response = await app.GetTestServer().CreateClient().SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal(ODataErrorCodes.ReadOnlySet,
                JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task The_metadata_describes_what_the_view_returns_and_that_nothing_is_written()
    {
        var app = await StartAsync();
        try
        {
            var document = XDocument.Parse(await app.GetTestServer().CreateClient().GetStringAsync("/$data/$metadata"));
            var entityType = document.Descendants(Edm + "EntityType").Single(t => (string?)t.Attribute("Name") == nameof(FreightSummary));

            // Exactly the model's own properties — no audit timestamps the view does not have.
            Assert.Equal(["Carrier", "Id", "Parcels"],
                entityType.Elements(Edm + "Property").Select(p => (string)p.Attribute("Name")!).Order(StringComparer.Ordinal));

            // Every property but the key is one a write cannot store.
            static bool Computed(XElement property) => property.Elements(Edm + "Annotation")
                .Any(a => (string?)a.Attribute("Term") == "Org.OData.Core.V1.Computed");
            Assert.All(entityType.Elements(Edm + "Property").Where(p => (string?)p.Attribute("Name") != "Id"),
                p => Assert.True(Computed(p), (string?)p.Attribute("Name")));

            // The set advertises all three restrictions.
            var terms = document.Descendants(Edm + "EntitySet").Single(s => (string?)s.Attribute("Name") == Set)
                .Elements(Edm + "Annotation").Select(a => (string)a.Attribute("Term")!).Order(StringComparer.Ordinal);
            Assert.Equal(
                [
                    "Org.OData.Capabilities.V1.DeleteRestrictions",
                    "Org.OData.Capabilities.V1.InsertRestrictions",
                    "Org.OData.Capabilities.V1.UpdateRestrictions",
                ],
                terms);
        }
        finally { await app.DisposeAsync(); }
    }
}
