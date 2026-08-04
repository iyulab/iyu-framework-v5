using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

// NOTE: these entities/context/controller are deliberately TOP-LEVEL public types.
// A nested controller is not `IsPublic` and MVC's ControllerFeatureProvider skips
// it regardless of application parts — which would make the routing assertion pass
// or fail for the wrong reason. Top-level types isolate the ApplicationPart concern.

public sealed class RoutingWidget : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class RoutingWidgetExt : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class RoutingWidgetContext(DbContextOptions<RoutingWidgetContext> options) : IyuDbContext(options)
{
    public DbSet<RoutingWidget> Widgets => Set<RoutingWidget>();
    public DbSet<RoutingWidgetExt> WidgetsExt => Set<RoutingWidgetExt>();
}

public sealed class RoutingWidgetsController(RoutingWidgetContext ctx)
    : IyuODataController<RoutingWidgetExt, RoutingWidget>(ctx);

/// <summary>
/// End-to-end OData routing over an in-memory <see cref="TestServer"/>. Unlike
/// <see cref="IyuODataControllerTests"/> (which invoke action methods directly),
/// these exercise the real MVC/OData routing pipeline that <c>AddIyuMainServer</c>
/// wires up — the layer where a missing ApplicationPart surfaces as a silent 404.
/// </summary>
/// <remarks>
/// Regression guard for the TestServer trap: the generated OData controllers live
/// in a non-entry assembly, so MVC's default entry-assembly part discovery (entry =
/// <c>testhost</c> under the test runner) never finds them. <c>AddIyuMainServer</c>
/// must register the controller-hosting assemblies as application parts itself.
/// </remarks>
public class ODataTestServerRoutingTests
{
    private static void RegisterViaMethodGroup(IyuMainServerOptions options)
        => options.ODataModel.AddEntityPair<RoutingWidgetExt, RoutingWidget>("RoutingWidgets");

    private static async Task<WebApplication> StartAsync(
        Action<IyuMainServerOptions> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<RoutingWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("routing-" + Guid.NewGuid().ToString("N")),
            configure: configure);

        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// <c>$metadata</c> and the service document answer under a test host, not just in production.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are served by OData's own <c>MetadataController</c>, which is never the entry assembly.
    /// Production reaches it through the entry assembly's dependency graph — measured: an app whose
    /// entry assembly is itself has <c>Microsoft.AspNetCore.OData</c> among its application parts
    /// and answers <c>$metadata</c> with 200. Under this test host the graph is the runner's, so
    /// the route was published and nothing answered it.
    /// </para>
    /// <para>
    /// A 404 there reads as a modelling mistake rather than a hosting artifact, which is why
    /// <c>AddIyuMainServer</c> registers that assembly rather than leaving it to a note. This test
    /// is what would notice if it stopped.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("/$data/$metadata")]
    [InlineData("/$data/")]
    public async Task MetadataAndServiceDocument_Answer_UnderATestHost(string path)
    {
        var app = await StartAsync(RegisterViaMethodGroup);
        try
        {
            using var resp = await app.GetTestServer().CreateClient().GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("RoutingWidget", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task EntitySet_IsRouted_WhenRegisteredViaMethodGroup()
    {
        // The callback is a method group, so its declaring assembly is the
        // controller-hosting assembly — AddIyuMainServer must auto-register it.
        var app = await StartAsync(RegisterViaMethodGroup);
        try
        {
            using var resp = await app.GetTestServer().CreateClient().GetAsync("/$data/RoutingWidgets");
            // Empty set, but the endpoint MUST resolve. A 404 means the controller
            // assembly was never registered as an application part.
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task EntitySet_IsRouted_WhenAssemblyGivenExplicitly()
    {
        // A lambda wrapper's declaring assembly resolves to the caller, not the
        // controller assembly — the explicit ControllerAssemblies escape hatch is
        // the predictable override for that case.
        var app = await StartAsync(options =>
        {
            options.ControllerAssemblies.Add(typeof(RoutingWidgetsController).Assembly);
            options.ODataModel.AddEntityPair<RoutingWidgetExt, RoutingWidget>("RoutingWidgets");
        });
        try
        {
            using var resp = await app.GetTestServer().CreateClient().GetAsync("/$data/RoutingWidgets");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task AlreadyDiscoveredAssembly_IsNotRegisteredTwice()
    {
        // Simulates production: the controller assembly is ALREADY a default part
        // (entry assembly = server assembly). AddIyuMainServer's candidate set
        // includes that same assembly, so the dedup guard must skip it — otherwise
        // a duplicate AssemblyPart yields duplicate controller types / ambiguous
        // actions at routing time.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var controllerAssembly = typeof(RoutingWidgetsController).Assembly;
        builder.Services.AddControllers().PartManager.ApplicationParts
            .Add(new AssemblyPart(controllerAssembly)); // pre-seed as if default discovery found it

        builder.Services.AddIyuMainServer<RoutingWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("routing-" + Guid.NewGuid().ToString("N")),
            configure: RegisterViaMethodGroup);

        var app = builder.Build();
        try
        {
            var partManager = app.Services.GetRequiredService<ApplicationPartManager>();
            var count = partManager.ApplicationParts
                .OfType<AssemblyPart>()
                .Count(p => p.Assembly == controllerAssembly);
            Assert.Equal(1, count); // exactly one — the guard skipped the duplicate

            app.UseIyuMainServer();
            await app.StartAsync();
            using var resp = await app.GetTestServer().CreateClient().GetAsync("/$data/RoutingWidgets");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // still routes, no ambiguity
        }
        finally { await app.DisposeAsync(); }
    }
}
