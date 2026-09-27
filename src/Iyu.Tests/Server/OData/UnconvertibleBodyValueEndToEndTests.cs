using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Iyu.Core.Entities;
using Iyu.Data;
using Iyu.MainServer;
using Iyu.Server.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iyu.Tests.Server.OData;

public enum TypedWidgetKind { Plain, Fancy }

public sealed class TypedWidget : IyuEntity
{
    public Guid TemplateId { get; set; }
    public int Seq { get; set; }
    public bool Flag { get; set; }
    public DateTimeOffset At { get; set; }
    public TypedWidgetKind Kind { get; set; }
    public string Label { get; set; } = "";
}

public sealed class TypedWidgetExt : IyuEntity
{
    public Guid TemplateId { get; set; }
    public int Seq { get; set; }
    public bool Flag { get; set; }
    public DateTimeOffset At { get; set; }
    public TypedWidgetKind Kind { get; set; }
    public string Label { get; set; } = "";
}

public sealed class TypedWidgetContext(DbContextOptions<TypedWidgetContext> options) : IyuDbContext(options)
{
    public DbSet<TypedWidget> Widgets => Set<TypedWidget>();
    public DbSet<TypedWidgetExt> WidgetsExt => Set<TypedWidgetExt>();
}

public sealed class TypedWidgetsController(TypedWidgetContext ctx)
    : IyuODataController<TypedWidgetExt, TypedWidget>(ctx);

/// <summary>
/// A write body whose value cannot be converted to its property's declared type is refused with the
/// property's path as the <c>target</c> and the declared type in a fixed sentence — never the value
/// the caller sent, never the reader's own message — and the cause is logged for the operator.
/// </summary>
/// <remarks>
/// Before this, the refusal said only "the value could not be converted to its expected type", with
/// no target: a caller had to remove fields one at a time to find the one at fault, which is the
/// procedure <c>UnknownProperty</c> had already removed for undeclared names in the same contract.
/// </remarks>
public class UnconvertibleBodyValueEndToEndTests
{
    private sealed class RecordingProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(RecordingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Entries.Enqueue((logLevel, formatter(state, exception), exception));
        }
    }

    private static async Task<(WebApplication App, RecordingProvider Log)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var log = new RecordingProvider();
        builder.Logging.AddProvider(log);
        builder.Services.AddIyuMainServer<TypedWidgetContext>(
            configureDb: db => db.UseInMemoryDatabase("unconvertible-" + Guid.NewGuid().ToString("N")),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(TypedWidgetsController).Assembly);
                options.ODataModel.AddEntityPair<TypedWidgetExt, TypedWidget>("TypedWidgets");
            });
        var app = builder.Build();
        app.UseIyuMainServer();
        await app.StartAsync();
        return (app, log);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed record Refusal(HttpStatusCode Status, string Code, IReadOnlyList<(string? Target, string Message)> Details, string Raw);

    private static async Task<Refusal> ReadAsync(HttpResponseMessage resp)
    {
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var error = doc.RootElement.GetProperty("error");
        var details = error.TryGetProperty("details", out var d)
            ? d.EnumerateArray()
                .Select(e => (e.TryGetProperty("target", out var t) ? t.GetString() : null, e.GetProperty("message").GetString()!))
                .ToList()
            : [];
        return new Refusal(resp.StatusCode, error.GetProperty("code").GetString()!, details, raw);
    }

    [Theory]
    [InlineData("""{"TemplateId":"not-a-guid","Label":"x"}""", "TemplateId", "Guid", "not-a-guid")]
    [InlineData("""{"Seq":"not-an-int","Label":"x"}""", "Seq", "Int32", "not-an-int")]
    [InlineData("""{"Seq":3000000000,"Label":"x"}""", "Seq", "Int32", "3000000000")]
    [InlineData("""{"Flag":"yes","Label":"x"}""", "Flag", "Boolean", "yes")]
    [InlineData("""{"At":"2026-08-19 10:00","Label":"x"}""", "At", "DateTimeOffset", "2026-08-19 10:00")]
    public async Task Post_with_an_unconvertible_value_names_the_property_and_its_type(
        string body, string target, string edmType, string literal)
    {
        var (app, _) = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync("/$data/TypedWidgets", Json(body));
            var refusal = await ReadAsync(resp);

            Assert.Equal(HttpStatusCode.BadRequest, refusal.Status);
            Assert.Equal(ODataErrorCodes.InvalidBody, refusal.Code);
            var detail = Assert.Single(refusal.Details);
            Assert.Equal(target, detail.Target);
            Assert.Equal($"The value could not be converted to {edmType}.", detail.Message);
            // The value the caller sent is not echoed back, and neither is the reader's own text or
            // the EDM namespace the sanitizer has always kept out of this answer.
            Assert.DoesNotContain("Edm.", refusal.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain(literal, refusal.Raw, StringComparison.Ordinal);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task An_unknown_enum_member_names_the_property()
    {
        var (app, _) = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                "/$data/TypedWidgets", Json("""{"Kind":"Gaudy","Label":"x"}"""));
            var refusal = await ReadAsync(resp);

            Assert.Equal(ODataErrorCodes.InvalidBody, refusal.Code);
            var detail = Assert.Single(refusal.Details);
            Assert.Equal("Kind", detail.Target);
            Assert.Equal("The value is not a member of TypedWidgetKind.", detail.Message);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Patch_with_an_unconvertible_value_names_the_property()
    {
        var (app, _) = await StartAsync();
        try
        {
            var id = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TypedWidgetContext>();
                db.Widgets.Add(new TypedWidget { Id = id, Label = "seed" });
                await db.SaveChangesAsync();
            }

            using var resp = await app.GetTestServer().CreateClient().PatchAsync(
                $"/$data/TypedWidgets({id})", Json("""{"TemplateId":"nope"}"""));
            var refusal = await ReadAsync(resp);

            Assert.Equal(ODataErrorCodes.InvalidBody, refusal.Code);
            Assert.Equal("TemplateId", Assert.Single(refusal.Details).Target);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task The_cause_is_logged_for_the_operator()
    {
        var (app, log) = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync(
                "/$data/TypedWidgets", Json("""{"TemplateId":"not-a-guid","Label":"x"}"""));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Exception is not null
                && e.Message.Contains("TypedWidgets", StringComparison.Ordinal));
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_convertible_body_is_created()
    {
        // Control: the inspection runs only when the bind has failed, and must not refuse a body the
        // reader accepts.
        var (app, _) = await StartAsync();
        try
        {
            using var resp = await app.GetTestServer().CreateClient().PostAsync("/$data/TypedWidgets", Json(
                $$"""{"TemplateId":"{{Guid.NewGuid()}}","Seq":7,"Flag":true,"At":"2026-08-19T10:00:00+09:00","Kind":"Fancy","Label":"x"}"""));

            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }
        finally { await app.DisposeAsync(); }
    }
}
