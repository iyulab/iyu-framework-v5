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

/// <summary>Fails every save with whatever <see cref="Failure"/> produces.</summary>
public sealed class ClassifiedWidgetContext(DbContextOptions<ClassifiedWidgetContext> options) : IyuDbContext(options)
{
    public static Func<Exception> Failure { get; set; } = () => new DbUpdateException("unset");

    public DbSet<ClassifiedWidget> Widgets => Set<ClassifiedWidget>();
    public DbSet<ClassifiedWidgetExt> WidgetsExt => Set<ClassifiedWidgetExt>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw Failure();
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

    private static async Task<HttpResponseMessage> PostAsync(Func<Exception> failure)
    {
        ClassifiedWidgetContext.Failure = failure;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuMainServer<ClassifiedWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("classified-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(ClassifiedWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<ClassifiedWidgetExt, ClassifiedWidget>(Set);
            });
        await using var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        var response = await app.GetTestServer().CreateClient().PostAsJsonAsync($"/$data/{Set}", new { Name = "x" });
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static Func<Exception> Refused(string? sqlState)
        => () => new DbUpdateException("wrapper text that must not leak", new SqlStateException(sqlState));

    [Theory]
    [InlineData("23505", HttpStatusCode.Conflict, ODataErrorCodes.UniqueViolation)]
    [InlineData("23503", HttpStatusCode.Conflict, ODataErrorCodes.ForeignKeyViolation)]
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

    [Fact]
    public async Task A_concurrency_conflict_says_so()
    {
        using var response = await PostAsync(() => new DbUpdateConcurrencyException("must not leak"));

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
        using var response = await PostAsync(() => new WriteRuleViolationException("VatTypeLocked", "Cannot change VAT handling after invoicing."));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"VatTypeLocked\"", body, StringComparison.Ordinal);
        Assert.Contains("Cannot change VAT handling after invoicing.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_rule_refusal_needs_a_code()
    {
        Assert.Throws<ArgumentException>(() => new WriteRuleViolationException(" ", "message"));
    }
}
