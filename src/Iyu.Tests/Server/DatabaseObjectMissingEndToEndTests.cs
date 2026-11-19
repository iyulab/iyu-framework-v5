using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Iyu.Tests.Server.OData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iyu.Tests.Server;

// Top-level public types for the reason ODataTestServerRoutingTests documents: a nested controller is not IsPublic and
// MVC's ControllerFeatureProvider skips it.

public sealed class MissingTableThing : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class MissingTableThingExt : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class MissingTableContext(DbContextOptions<MissingTableContext> options) : IyuDbContext(options)
{
    public DbSet<MissingTableThing> Things => Set<MissingTableThing>();
    public DbSet<MissingTableThingExt> ThingsExt => Set<MissingTableThingExt>();
}

public sealed class MissingTableThingsController(MissingTableContext ctx)
    : IyuODataController<MissingTableThingExt, MissingTableThing>(ctx);

/// <summary>
/// A host whose database never had its schema applied answers a request with what is wrong — the database does not have an
/// object the host maps — instead of a bare <c>500</c> whose cause is only in the log. The classification is the same on
/// both surfaces and on a write; what the body says about the object is written only where details are wanted.
/// </summary>
public sealed class DatabaseObjectMissingEndToEndTests : IDisposable
{
    private const string ReadPolicy = "things.read";
    private const string Code = "DatabaseObjectMissing";

    private readonly SharedMemorySqlite _db = new();
    private readonly List<WebApplication> _apps = [];
    private readonly List<(string Category, LogLevel Level, Exception? Exception)> _log = [];

    public void Dispose()
    {
        foreach (var app in _apps) app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _db.Dispose();
    }

    private sealed class Capture(List<(string, LogLevel, Exception?)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, sink);
        public void Dispose() { }

        private sealed class Logger(string category, List<(string, LogLevel, Exception?)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { lock (sink) sink.Add((category, logLevel, exception)); }
        }
    }

    // createSchema: whether the schema was applied — the host that did what the README says.
    // environment: the host environment; null names none, which is Production.
    private async Task<HttpClient> HostAsync(
        bool createSchema = false, string? environment = "Production", bool? includeDetails = null, bool restrict = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new Capture(_log));
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy(ReadPolicy, p => p.RequireClaim("perm", ReadPolicy)));
        builder.Services.AddIyuMainServer<MissingTableContext>(
            configureDb: db => db.UseSqlite(_db.ConnectionString),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(MissingTableThingsController).Assembly);
                options.ODataModel.AddEntityPair<MissingTableThingExt, MissingTableThing>("MissingTableThings");
                options.GraphQL.AddEntityPair<MissingTableThingExt, MissingTableThing>("things", "thing");
                options.IncludeODataErrorDetails = includeDetails;
                if (restrict) options.Authorize<MissingTableThingExt>(read: ReadPolicy);
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseIyuMainServer();
        await app.StartAsync();
        _apps.Add(app);

        if (createSchema)
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MissingTableContext>().Database.EnsureCreatedAsync();
        }
        return app.GetTestServer().CreateClient();
    }

    private static async Task<(HttpStatusCode Status, string Body)> ODataGet(HttpClient client, string? perm = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/$data/MissingTableThings");
        if (perm is not null) request.Headers.Add("X-Test-Perm", perm);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpStatusCode Status, string Body)> GraphQL(HttpClient client, string query, string? perm = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query }) };
        if (perm is not null) request.Headers.Add("X-Test-Perm", perm);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string ODataCode(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString()!;
    private static string ODataMessage(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString()!;

    private static JsonElement GraphQLError(string body, int index = 0) => JsonDocument.Parse(body).RootElement.GetProperty("errors")[index].Clone();

    private static string GraphQLCode(string body)
        => GraphQLError(body).TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("code", out var code)
            ? code.GetString()!
            : "(no code) " + body;

    // ---- OData read ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_OData_read_names_the_missing_object_in_development()
    {
        var client = await HostAsync(environment: Environments.Development);

        var (status, body) = await ODataGet(client);

        Assert.True(status == HttpStatusCode.InternalServerError, body);
        Assert.Equal(Code, ODataCode(body));
        var message = ODataMessage(body);
        Assert.Contains("Things", message, StringComparison.Ordinal);       // the object, as the driver names it
        Assert.Contains("apply the schema", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase); // the provider's text stays in the log
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task An_OData_read_outside_development_carries_the_code_and_nothing_about_the_object(string? environment)
    {
        var client = await HostAsync(environment: environment);

        var (status, body) = await ODataGet(client);

        Assert.True(status == HttpStatusCode.InternalServerError, body);
        Assert.Equal(Code, ODataCode(body));
        Assert.DoesNotContain("Things", ODataMessage(body), StringComparison.Ordinal);
        Assert.DoesNotContain("schema", ODataMessage(body), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_details_setting_overrides_the_environment_in_both_directions()
    {
        var shown = await ODataGet(await HostAsync(environment: Environments.Production, includeDetails: true));
        var hidden = await ODataGet(await HostAsync(environment: Environments.Development, includeDetails: false));

        Assert.Contains("Things", ODataMessage(shown.Body), StringComparison.Ordinal);
        Assert.DoesNotContain("Things", ODataMessage(hidden.Body), StringComparison.Ordinal);
        Assert.Equal(Code, ODataCode(hidden.Body));
    }

    [Fact]
    public async Task A_host_whose_schema_was_applied_answers_as_before()
    {
        var client = await HostAsync(createSchema: true, environment: Environments.Development);

        var odata = await ODataGet(client);
        var graphql = await GraphQL(client, "{ things { nodes { name } } }");

        Assert.True(odata.Status == HttpStatusCode.OK, odata.Body);
        Assert.True(graphql.Status == HttpStatusCode.OK, graphql.Body);
        Assert.DoesNotContain("\"errors\"", graphql.Body, StringComparison.Ordinal);
    }

    // ---- GraphQL ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_GraphQL_query_names_the_missing_object_in_development()
    {
        var client = await HostAsync(environment: Environments.Development);

        var (status, body) = await GraphQL(client, "{ things { nodes { name } } }");

        Assert.Equal(HttpStatusCode.OK, status); // GraphQL answers failures inside a 200 unless the transport fails
        Assert.Equal(Code, GraphQLCode(body));
        var message = GraphQLError(body).GetProperty("message").GetString()!;
        Assert.Contains("Things", message, StringComparison.Ordinal);
        Assert.Contains("apply the schema", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_GraphQL_query_outside_development_carries_the_code_and_nothing_about_the_object()
    {
        var client = await HostAsync(environment: Environments.Production);

        var (_, body) = await GraphQL(client, "{ things { nodes { name } } }");

        Assert.Equal(Code, GraphQLCode(body));
        var message = GraphQLError(body).GetProperty("message").GetString()!;
        Assert.DoesNotContain("Things", message, StringComparison.Ordinal);
        Assert.DoesNotContain("schema", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_details_setting_governs_GraphQL_as_it_does_OData()
    {
        var shown = await GraphQL(await HostAsync(environment: Environments.Production, includeDetails: true), "{ things { nodes { name } } }");

        Assert.Contains("Things", GraphQLError(shown.Body).GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    // ---- writes -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_write_names_the_missing_object_too()
    {
        var client = await HostAsync(environment: Environments.Development);

        using var response = await client.PostAsJsonAsync("/$data/MissingTableThings", new { Name = "x" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, body);
        Assert.Equal(Code, ODataCode(body));
        Assert.Contains("Things", ODataMessage(body), StringComparison.Ordinal);
    }

    // ---- authorization ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refused_caller_is_refused_before_the_database_is_asked()
    {
        var client = await HostAsync(environment: Environments.Development, restrict: true);

        var odata = await ODataGet(client, perm: "something.else");
        var anonymous = await ODataGet(client);
        var graphql = await GraphQL(client, "{ things { nodes { name } } }", perm: "something.else");

        Assert.Equal(HttpStatusCode.Forbidden, odata.Status);
        Assert.Equal("Forbidden", ODataCode(odata.Body));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        Assert.Equal("Unauthorized", ODataCode(anonymous.Body));
        Assert.Equal("AUTH_NOT_AUTHORIZED", GraphQLCode(graphql.Body));
        Assert.DoesNotContain("Things", graphql.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_permitted_caller_reaches_the_database_and_is_told()
    {
        var client = await HostAsync(environment: Environments.Development, restrict: true);

        var odata = await ODataGet(client, perm: ReadPolicy);

        Assert.Equal(Code, ODataCode(odata.Body));
    }

    // ---- the log ------------------------------------------------------------------------------------------------

    // The Error entries that carry the provider's failure and are this framework's own — the entries EF Core or the
    // exception-handler middleware write for the same exception are theirs and not counted; they are named when the count is wrong.
    private (int Count, string All) ErrorsCarrying(string fragment)
    {
        lock (_log)
        {
            var carrying = _log.Where(e => e.Level == LogLevel.Error && e.Exception?.ToString().Contains(fragment, StringComparison.Ordinal) == true).ToList();
            return (carrying.Count(e => e.Category.StartsWith("Iyu.", StringComparison.Ordinal)),
                string.Join(" | ", carrying.Select(e => e.Category)));
        }
    }

    [Fact]
    public async Task An_OData_request_writes_the_provider_failure_to_the_log_once_at_error_level()
    {
        var client = await HostAsync(environment: Environments.Production);

        await ODataGet(client);

        var (count, all) = ErrorsCarrying("no such table");
        Assert.True(count == 1, all);
    }

    [Fact]
    public async Task A_GraphQL_request_writes_it_once_however_many_fields_failed()
    {
        var client = await HostAsync(environment: Environments.Production);

        var (_, body) = await GraphQL(client, "{ first: things { nodes { name } } second: things { nodes { name } } }");

        Assert.Equal(2, JsonDocument.Parse(body).RootElement.GetProperty("errors").GetArrayLength());
        var (count, all) = ErrorsCarrying("no such table");
        Assert.True(count == 1, all);
    }
}
