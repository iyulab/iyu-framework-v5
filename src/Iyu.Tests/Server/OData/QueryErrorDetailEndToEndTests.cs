using System.Net;
using System.Text.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Iyu.Tests.Server.OData;

// Top-level public types for the reason ODataTestServerRoutingTests documents: a nested controller
// is not IsPublic and MVC's ControllerFeatureProvider skips it.

public sealed class ErrDetailItem : IyuEntity { public string Name { get; set; } = ""; }
public sealed class ErrDetailItemExt : IyuEntity { public string Name { get; set; } = ""; }

public sealed class ErrDetailContext(DbContextOptions<ErrDetailContext> options) : IyuDbContext(options)
{
    public DbSet<ErrDetailItem> Items => Set<ErrDetailItem>();
    public DbSet<ErrDetailItemExt> ItemsExt => Set<ErrDetailItemExt>();
}

public sealed class ErrDetailItemsController(ErrDetailContext ctx) : IyuODataController<ErrDetailItemExt, ErrDetailItem>(ctx);

/// <summary>
/// What a query-option failure on <c>/$data</c> tells the caller, per host environment.
/// </summary>
/// <remarks>
/// The stock <c>EnableQueryAttribute</c> answers every such failure with the exception's type and
/// stack trace in <c>innererror</c>, in every environment. These tests pin that the message a
/// caller needs to fix the query survives everywhere, and that the part describing the server's
/// internals is written only where it is asked for.
/// </remarks>
public class QueryErrorDetailEndToEndTests
{
    private const string Set = "ErrDetailItems";

    private static async Task<(HttpStatusCode Status, JsonElement Error, string Body)> QueryAsync(
        string query, string? environment = null, bool? includeDetails = null)
    {
        var dbName = "errdetail-" + Guid.NewGuid().ToString("N");
        var builder = environment is null
            ? WebApplication.CreateBuilder()
            : WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();

        builder.Services.AddIyuMainServer<ErrDetailContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ErrDetailItemsController).Assembly);
                options.ODataModel.AddEntityPair<ErrDetailItemExt, ErrDetailItem>(Set);
                options.IncludeODataErrorDetails = includeDetails;
            });

        await using var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/$data/{Set}?{query}");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return (response.StatusCode, document.RootElement.GetProperty("error").Clone(), body);
    }

    /// <summary>
    /// The reported case: a host that is not Development answers an unknown property with a 400
    /// whose message still names the property — and nothing about the exception behind it.
    /// </summary>
    [Theory]
    [InlineData("$select=Nope")]
    [InlineData("$filter=Nope eq 1")]
    public async Task Outside_development_a_query_error_keeps_its_message_and_drops_the_stack_trace(string query)
    {
        var (status, error, body) = await QueryAsync(query, Environments.Production);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Could not find a property named 'Nope'", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(error.TryGetProperty("innererror", out _), body);
        Assert.DoesNotContain("stacktrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(Microsoft.OData.ODataException).FullName!, body, StringComparison.Ordinal);
        // The message still names the type it looked on. That is the EDM type name, which
        // $metadata already publishes to the same caller — not something this setting withholds.
    }

    /// <summary>
    /// A host built without naming an environment is Production — the default a deployed server
    /// most often runs under, so it is pinned on its own rather than through the named case above.
    /// </summary>
    [Fact]
    public async Task A_host_that_names_no_environment_withholds_the_details()
    {
        var (status, error, _) = await QueryAsync("$select=Nope");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(error.TryGetProperty("innererror", out _));
    }

    /// <summary>
    /// The negative control: in Development the details are still written — which is also what
    /// shows the assertions above measure the serializer, not a query that never failed the way
    /// they assume.
    /// </summary>
    [Fact]
    public async Task In_development_the_details_are_written()
    {
        var (status, error, _) = await QueryAsync("$select=Nope", Environments.Development);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(error.TryGetProperty("innererror", out var inner));
        Assert.True(inner.TryGetProperty("stacktrace", out _));
    }

    /// <summary>The setting overrides the environment in both directions.</summary>
    [Theory]
    [InlineData("Production", true, true)]
    [InlineData("Development", false, false)]
    public async Task The_setting_overrides_the_environment(string environment, bool includeDetails, bool expectInnerError)
    {
        var (_, error, _) = await QueryAsync("$select=Nope", environment, includeDetails);

        Assert.Equal(expectInnerError, error.TryGetProperty("innererror", out _));
    }
}
