using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.Data.WriteRules;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

public sealed class ClassifiedWidget : IyuEntity
{
    public string Name { get; set; } = "";
}

public sealed class ClassifiedWidgetExt : IyuEntity
{
    public string Name { get; set; } = "";
}

/// <summary>A provider's exception the way ADO.NET exposes the standard error class — <see cref="DbException.SqlState"/>.</summary>
public sealed class SqlStateException(string? sqlState) : DbException("provider text that must not leak to the client")
{
    public override string? SqlState { get; } = sqlState;
}

/// <summary>
/// Fails every asynchronous save with whatever <see cref="Failure"/> produces from this context — so a failure can carry the
/// entries the save was writing, as EF's own does. The synchronous save is left working, for seeding.
/// </summary>
public sealed class ClassifiedWidgetContext(DbContextOptions<ClassifiedWidgetContext> options) : IyuDbContext(options)
{
    public static Func<ClassifiedWidgetContext, Exception> Failure { get; set; } = _ => new DbUpdateException("unset");

    public DbSet<ClassifiedWidget> Widgets => Set<ClassifiedWidget>();
    public DbSet<ClassifiedWidgetExt> WidgetsExt => Set<ClassifiedWidgetExt>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw Failure(this);
}

public sealed class ClassifiedWidgetsController(ClassifiedWidgetContext ctx)
    : IyuODataController<ClassifiedWidgetExt, ClassifiedWidget>(ctx);

/// <summary>
/// A failed write says what kind of failure it was. The data store's refusal is classified by the standard SQLSTATE
/// class ADO.NET exposes (<see cref="DbException.SqlState"/>) — no provider package needed — and a write rule refuses
/// with its own code. Anything unclassified stays the provider-neutral <c>409 Conflict</c>.
/// </summary>
[Collection(nameof(WriteFailureClassificationEndToEndTests))]
public class WriteFailureClassificationEndToEndTests
{
    private const string Set = "ClassifiedWidgets";

    private static Task<HttpResponseMessage> PostAsync(Func<ClassifiedWidgetContext, Exception> failure)
        => SendAsync(failure, client => client.PostAsJsonAsync($"/$data/{Set}", new { Name = "x" }));

    private static async Task<HttpResponseMessage> SendAsync(
        Func<ClassifiedWidgetContext, Exception> failure, Func<HttpClient, Task<HttpResponseMessage>> send, Guid? seed = null,
        Action<IServiceCollection>? services = null)
    {
        ClassifiedWidgetContext.Failure = failure;
        // One database per app: configureDb runs per context, so a name made inside it would give each scope its own.
        var dbName = "classified-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        services?.Invoke(builder.Services);
        builder.Services.AddIyuMainServer<ClassifiedWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ClassifiedWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<ClassifiedWidgetExt, ClassifiedWidget>(Set);
            });
        await using var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        if (seed is { } id)
        {
            using var scope = app.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ClassifiedWidgetContext>();
            ctx.WidgetsExt.Add(new ClassifiedWidgetExt { Id = id, Name = "seed" });
            ctx.Widgets.Add(new ClassifiedWidget { Id = id, Name = "seed" });
            ctx.SaveChanges();
        }
        var response = await send(app.GetTestServer().CreateClient());
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    /// <summary>The provider's refusal, carrying the entries the save was writing — as EF's own exception does.</summary>
    private static Func<ClassifiedWidgetContext, Exception> Refused(string? sqlState)
        => ctx => new DbUpdateException("wrapper text that must not leak", new SqlStateException(sqlState),
            ctx.ChangeTracker.Entries().ToList());

    [Theory]
    [InlineData("23505", HttpStatusCode.Conflict, ODataErrorCodes.UniqueViolation)]
    [InlineData("23503", HttpStatusCode.BadRequest, ODataErrorCodes.ReferenceMissing)] // a create removes nothing
    [InlineData("23502", HttpStatusCode.BadRequest, ODataErrorCodes.RequiredValueMissing)]
    [InlineData("22001", HttpStatusCode.BadRequest, ODataErrorCodes.ValueTooLong)]
    public async Task A_refusal_with_a_standard_sqlstate_says_which_kind_it_is(string sqlState, HttpStatusCode status, string code)
    {
        using var response = await PostAsync(Refused(sqlState));

        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"code\":\"{code}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("must not leak", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("40001")] // a class this handler does not classify
    [InlineData(null)]    // a provider that does not report SQLSTATE
    public async Task An_unclassified_refusal_stays_the_neutral_conflict(string? sqlState)
    {
        using var response = await PostAsync(Refused(sqlState));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains($"\"code\":\"{ODataErrorCodes.Conflict}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>A provider that reports its own error number, not SQLSTATE — named by what it means.</summary>
    private sealed class NumberedException(int number) : DbException("provider text that must not leak to the client")
    {
        public int Number { get; } = number;
    }

    private sealed class NumberTranslator : ISqlStateTranslator
    {
        public string? SqlStateOf(DbException exception) => exception is NumberedException { Number: 2627 } ? "23505" : null;
    }

    /// <summary>
    /// A provider that reports no SQLSTATE is classified by a registered translator, through the same classification —
    /// and one the translator does not know stays the neutral conflict.
    /// </summary>
    [Theory]
    [InlineData(2627, HttpStatusCode.Conflict, ODataErrorCodes.UniqueViolation)]
    [InlineData(9999, HttpStatusCode.Conflict, ODataErrorCodes.Conflict)]
    public async Task A_provider_without_sqlstate_is_classified_by_a_registered_translator(
        int number, HttpStatusCode status, string code)
    {
        using var response = await SendAsync(
            ctx => new DbUpdateException("wrapper text that must not leak", new NumberedException(number),
                ctx.ChangeTracker.Entries().ToList()),
            client => client.PostAsJsonAsync($"/$data/{Set}", new { Name = "x" }),
            services: s => s.AddSingleton<ISqlStateTranslator, NumberTranslator>());

        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"code\":\"{code}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("must not leak", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_concurrency_conflict_says_so()
    {
        using var response = await PostAsync(_ => new DbUpdateConcurrencyException("must not leak"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains($"\"code\":\"{ODataErrorCodes.ConcurrencyConflict}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A write rule refuses with a code and a message written for the caller — the response carries both, so a client
    /// tells one rule from another without parsing text, and the application needs no handler of its own for it.
    /// </summary>
    [Fact]
    public async Task A_write_rule_refusal_carries_the_rule_s_code_and_message()
    {
        using var response = await PostAsync(_ => new WriteRuleViolationException("VatTypeLocked", "Cannot change VAT handling after invoicing."));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"VatTypeLocked\"", body, StringComparison.Ordinal);
        Assert.Contains("Cannot change VAT handling after invoicing.", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same SQLSTATE on a delete is the other failure: the row is still referred to. That is the data's state, not the
    /// request's value — a conflict, which clearing the references resolves.
    /// </summary>
    [Fact]
    public async Task A_foreign_key_refusal_of_a_delete_is_a_conflict_with_the_references_still_there()
    {
        var id = Guid.NewGuid();
        using var response = await SendAsync(Refused("23503"), client => client.DeleteAsync($"/$data/{Set}({id})"), seed: id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"code\":\"{ODataErrorCodes.ReferencedByOthers}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("must not leak", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_rule_refusal_needs_a_code()
    {
        Assert.Throws<ArgumentException>(() => new WriteRuleViolationException(" ", "message"));
    }
}
