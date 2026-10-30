using Iyu.Lowline;
using Lowline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Lowline;

public sealed class AddIyuLowlineTests : IDisposable
{
    // A form with one decided field: what colour an order is, learned from the colours people settled on.
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    // A file rather than a shared in-memory connection: after a save the engine chooses its thresholds again on a
    // background worker, which opens its own context beside the one the test is using — as it does on a real database.
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private WebApplicationBuilder Host(string environment = "Production")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        return builder;
    }

    private static void Tables(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();
    }

    [Fact]
    public void Nothing_is_registered_until_AddIyuLowline_is_called()
    {
        var builder = Host();
        using var app = builder.Build();

        Assert.Null(app.Services.GetService<LowlineForms>());
        Assert.Null(app.Services.GetService<ILowlineStore>());
    }

    [Fact]
    public async Task A_host_that_turned_it_on_without_a_form_does_not_start()
    {
        var builder = Host();
        builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(_ => { });
        await using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        var message = Assert.Single(refusal.Failures);
        Assert.Contains("no form is defined", message);
        Assert.Contains("Engine.Form", message);
    }

    [Fact]
    public async Task A_form_the_engine_refuses_stops_the_host_as_it_starts()
    {
        var builder = Host();
        builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(
            o => o.Engine.Form("order", "this is not a definition"));
        await using var app = builder.Build();

        await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());
    }

    [Fact]
    public async Task A_host_with_a_form_starts_and_serves_one_set_of_forms_over_the_application_tables()
    {
        var builder = Host();
        builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o => o.Engine.Form("order", OrderForm));
        await using var app = builder.Build();
        await app.StartAsync();
        Tables(app);

        var forms = app.Services.GetRequiredService<LowlineForms>();
        Assert.Same(forms, app.Services.GetRequiredService<LowlineForms>());
        Assert.IsType<EfLowlineStore<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>>(app.Services.GetRequiredService<ILowlineStore>());

        var scope = new LowlineScope("order", "plant-a");
        await forms.SettleAsync(
            scope, "r1",
            new Dictionary<string, FieldValue> { ["customer"] = "acme", ["colour"] = "red" },
            draft: null, SettledBy.Person, attestation: null);

        using var db = app.Services.CreateScope();
        var row = await db.ServiceProvider.GetRequiredService<LowlineTestDb>().Records.SingleAsync();
        Assert.Equal(("order", "plant-a", "r1"), (row.Form, row.Space, row.RecordId));
        Assert.False(string.IsNullOrWhiteSpace(row.Json));
    }

    [Fact]
    public async Task A_restarted_host_learns_again_from_the_records_it_stored()
    {
        var scope = new LowlineScope("order", "plant-a");

        // First life: forty people settle acme orders as red.
        {
            var builder = Host();
            builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o => o.Engine.Form("order", OrderForm));
            await using var app = builder.Build();
            await app.StartAsync();
            Tables(app);
            var forms = app.Services.GetRequiredService<LowlineForms>();
            for (var i = 0; i < 40; i++)
            {
                await forms.SettleAsync(
                    scope, $"r{i}",
                    new Dictionary<string, FieldValue> { ["customer"] = "acme", ["colour"] = "red" },
                    draft: null, SettledBy.Person, attestation: null);
            }

            await forms.WhenTunedAsync(scope);
        }

        // Second life: a new host over the same tables answers from what the first one stored.
        {
            var builder = Host();
            builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o => o.Engine.Form("order", OrderForm));
            await using var app = builder.Build();
            await app.StartAsync();
            var forms = app.Services.GetRequiredService<LowlineForms>();

            var proposals = await forms.ProposeAsync(
                scope, "new-order",
                new Dictionary<string, FieldValue> { ["customer"] = "acme" },
                allowed: null);

            var colour = Assert.Single(proposals, p => p.Field == "colour");
            Assert.True(colour.Suggestion is not null, $"no suggestion: {colour.Abstain}");
            Assert.Equal("red", colour.Suggestion?.Value);
        }
    }

    [Fact]
    public void The_whole_container_validates_in_Development()
    {
        var builder = Host("Development");
        builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o => o.Engine.Form("order", OrderForm));

        using var app = builder.Build();
    }
}
