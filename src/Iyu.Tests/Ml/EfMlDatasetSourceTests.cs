using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Claims;
using Iyu.Ml;
using Iyu.Ml.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Ml;

public class MlReading
{
    public int Id { get; set; }

    public string Plant { get; set; } = "";

    public double Temperature { get; set; }

    public double? Pressure { get; set; }

    public string Kind { get; set; } = "";

    public string Secret { get; set; } = "";

    public bool Active { get; set; } = true;

    [NotMapped]
    public string Computed => Plant + Kind;
}

public class NotInTheModel
{
    public int Id { get; set; }

    public double Value { get; set; }
}

public class MlTestDb(DbContextOptions<MlTestDb> options) : DbContext(options)
{
    public DbSet<MlReading> Readings => Set<MlReading>();
}

/// <summary>Reads datasets from the application's own tables, bounded by the caller and by the host's read guard.</summary>
public sealed class EfMlDatasetSourceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-ml-{Guid.NewGuid():N}.db");
    private readonly List<string> _sql = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private static readonly ClaimsPrincipal Analyst = new(new ClaimsIdentity([new Claim("sub", "analyst")], "test"));

    private sealed class RecordingGuard : IMlReadGuard
    {
        public HashSet<(Type, string)> Refused { get; } = [];

        public bool MayRead { get; set; } = true;

        public List<(Type Entity, ClaimsPrincipal Caller)> Asked { get; } = [];

        public string? RefusalFor(Type entity, string property) =>
            Refused.Contains((entity, property)) ? "a read policy guards it" : null;

        public ValueTask<bool> MayReadAsync(Type entity, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
        {
            Asked.Add((entity, caller));
            return ValueTask.FromResult(MayRead);
        }
    }

    private HostApplicationBuilder Host()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        builder.Services.AddDbContext<MlTestDb>(o => o.UseSqlite($"Data Source={_file}").LogTo(_sql.Add, LogLevel.Information));
        return builder;
    }

    private static void Seed(IHost app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MlTestDb>();
        db.Database.EnsureCreated();
        db.Readings.AddRange(
            new MlReading { Plant = "a", Temperature = 20.5, Pressure = 1.5, Kind = "ok", Secret = "s1" },
            new MlReading { Plant = "b", Temperature = 21.5, Pressure = null, Kind = "bad", Secret = "s2" },
            new MlReading { Plant = "c", Temperature = 99, Pressure = 3, Kind = "ok", Secret = "s3", Active = false });
        db.SaveChanges();
    }

    private static void Declare(HostApplicationBuilder builder, Action<MlDatasetBuilder<MlReading>>? columns = null) =>
        builder.Services.AddIyuMlEntityFramework<MlTestDb>(ef => ef.Dataset("readings", columns ?? (d => d
            .Feature(r => r.Temperature)
            .Feature(r => r.Plant, "plant_code")
            .Label(r => r.Kind)
            .Where(r => r.Active))));

    private static async Task<List<IReadOnlyDictionary<string, object?>>> Collect(IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var all = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in rows) all.Add(row);
        return all;
    }

    // ---- reading ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rows_come_back_keyed_by_the_declared_column_names_and_only_those_the_filter_keeps()
    {
        var builder = Host();
        Declare(builder);
        builder.Services.AddIyuMl(o => o.NoReadPolicies());
        using var app = builder.Build();
        Seed(app);
        await app.StartAsync();

        var rows = await Collect(app.Services.GetRequiredService<IMlDatasetSource>().ReadAsync("readings", Analyst));

        Assert.Equal(2, rows.Count);
        Assert.Equal(["Temperature", "plant_code", "Kind"], rows[0].Keys.OrderBy(k => k == "Temperature" ? 0 : k == "plant_code" ? 1 : 2));
        Assert.Equal(20.5, rows[0]["Temperature"]);
        Assert.Equal("a", rows[0]["plant_code"]);
        Assert.Equal("bad", rows[1]["Kind"]);
        await app.StopAsync();
    }

    [Fact]
    public async Task A_property_the_dataset_does_not_declare_is_never_selected_from_the_database()
    {
        var builder = Host();
        Declare(builder);
        builder.Services.AddIyuMl(o => o.NoReadPolicies());
        using var app = builder.Build();
        Seed(app);
        await app.StartAsync();
        _sql.Clear();

        await Collect(app.Services.GetRequiredService<IMlDatasetSource>().ReadAsync("readings", Analyst));

        var select = Assert.Single(_sql, s => s.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && s.Contains("Readings"));
        Assert.Contains("Temperature", select);
        Assert.DoesNotContain("Secret", select);
        Assert.DoesNotContain("Pressure", select);
        await app.StopAsync();
    }

    [Fact]
    public async Task A_nullable_column_arrives_as_null_and_the_rows_export_to_csv_and_fit_a_baseline()
    {
        var builder = Host();
        Declare(builder, d => d.Feature(r => r.Temperature).Feature(r => r.Pressure).Label(r => r.Kind));
        builder.Services.AddIyuMl(o => o.NoReadPolicies());
        using var app = builder.Build();
        Seed(app);
        await app.StartAsync();
        var source = app.Services.GetRequiredService<IMlDatasetSource>();
        var definition = source.Datasets.Single();

        var writer = new StringWriter();
        var count = await MlCsv.WriteAsync(definition, source.ReadAsync("readings", Analyst), writer);
        var baseline = await RobustZBaseline.FitAsync("temperature", "v1", definition, source.ReadAsync("readings", Analyst));

        Assert.Equal(3, count);
        Assert.Equal("Temperature,Pressure,Kind\r\n20.5,1.5,ok\r\n21.5,,bad\r\n99,3,ok\r\n", writer.ToString());
        Assert.Equal(2, baseline.Features.Count);
        await app.StopAsync();
    }

    [Fact]
    public async Task An_unknown_dataset_and_an_anonymous_caller_are_refused_at_the_call_not_at_the_first_row()
    {
        var builder = Host();
        Declare(builder);
        builder.Services.AddIyuMl(o => o.NoReadPolicies());
        using var app = builder.Build();
        Seed(app);
        await app.StartAsync();
        var source = app.Services.GetRequiredService<IMlDatasetSource>();

        Assert.Throws<ArgumentException>(() => source.ReadAsync("nothing", Analyst));
        Assert.Throws<UnauthorizedAccessException>(() => source.ReadAsync("readings", new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Throws<UnauthorizedAccessException>(() => source.ReadAsync("readings", new ClaimsPrincipal()));
        await app.StopAsync();
    }

    // ---- the read guard --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_caller_the_guard_says_may_not_read_the_entity_gets_no_rows_and_the_database_is_not_asked()
    {
        var guard = new RecordingGuard { MayRead = false };
        var builder = Host();
        Declare(builder);
        builder.Services.AddSingleton<IMlReadGuard>(guard);
        builder.Services.AddIyuMl(_ => { });
        using var app = builder.Build();
        Seed(app);
        await app.StartAsync();
        _sql.Clear();

        var rows = await Collect(app.Services.GetRequiredService<IMlDatasetSource>().ReadAsync("readings", Analyst));

        Assert.Empty(rows);
        Assert.Equal([(typeof(MlReading), Analyst)], guard.Asked);
        Assert.DoesNotContain(_sql, s => s.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && s.Contains("Readings"));
        await app.StopAsync();
    }

    [Fact]
    public async Task A_caller_the_guard_allows_gets_the_rows()
    {
        var guard = new RecordingGuard();
        var builder = Host();
        Declare(builder);
        builder.Services.AddSingleton<IMlReadGuard>(guard);
        builder.Services.AddIyuMl(_ => { });
        using var app = builder.Build();
        Seed(app);
        await app.StartAsync();

        var rows = await Collect(app.Services.GetRequiredService<IMlDatasetSource>().ReadAsync("readings", Analyst));

        Assert.Equal(2, rows.Count);
        await app.StopAsync();
    }

    [Fact]
    public async Task A_host_does_not_start_when_the_guard_refuses_a_column()
    {
        var guard = new RecordingGuard();
        guard.Refused.Add((typeof(MlReading), "Kind"));
        var builder = Host();
        Declare(builder);
        builder.Services.AddSingleton<IMlReadGuard>(guard);
        builder.Services.AddIyuMl(_ => { });
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("MlReading.Kind", refusal.Message);
        Assert.Contains("label", refusal.Message);
        Assert.Contains("a read policy guards it", refusal.Message);
        Assert.DoesNotContain("MlReading.Temperature", refusal.Message);
    }

    [Fact]
    public async Task A_column_the_guard_refuses_is_refused_at_every_read_even_when_the_host_never_started()
    {
        var guard = new RecordingGuard();
        guard.Refused.Add((typeof(MlReading), "Kind"));
        var builder = Host();
        Declare(builder);
        builder.Services.AddSingleton<IMlReadGuard>(guard);
        builder.Services.AddIyuMl(_ => { });
        using var app = builder.Build();
        Seed(app);
        // No StartAsync: the start-up check never runs, as with a service provider assembled by hand.

        var source = app.Services.GetRequiredService<IMlDatasetSource>();
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Collect(source.ReadAsync("readings", Analyst)));

        Assert.Contains("MlReading.Kind", refusal.Message);
        // A guard that changes its mind is heard: nothing is cached from an earlier answer.
        guard.Refused.Clear();
        Assert.Equal(2, (await Collect(source.ReadAsync("readings", Analyst))).Count);
    }

    [Fact]
    public async Task A_property_that_only_the_filter_reads_is_a_read_too()
    {
        var guard = new RecordingGuard();
        guard.Refused.Add((typeof(MlReading), "Secret"));
        var builder = Host();
        Declare(builder, d => d.Feature(r => r.Temperature).Label(r => r.Kind).Where(r => r.Secret == "s1"));
        builder.Services.AddSingleton<IMlReadGuard>(guard);
        builder.Services.AddIyuMl(_ => { });
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("MlReading.Secret", refusal.Message);
        Assert.Contains("filter", refusal.Message);
    }

    // ---- the declaration -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_host_does_not_start_when_an_entity_or_a_property_is_outside_the_model()
    {
        var builder = Host();
        builder.Services.AddIyuMlEntityFramework<MlTestDb>(ef => ef
            .Dataset<MlReading>("computed", d => d.Feature(r => r.Computed).Label(r => r.Kind))
            .Dataset<NotInTheModel>("elsewhere", d => d.Feature(r => r.Value)));
        builder.Services.AddIyuMl(o => o.NoReadPolicies());
        using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("MlReading.Computed is not a mapped column", refusal.Message);
        Assert.Contains("NotInTheModel is not part of the MlTestDb model", refusal.Message);
    }

    [Fact]
    public void A_column_is_a_plain_property_of_the_entity_and_not_a_calculation()
    {
        var builder = new MlDatasetBuilder<MlReading>();

        Assert.Throws<ArgumentException>(() => builder.Feature(r => r.Temperature + 1));
        Assert.Throws<ArgumentException>(() => builder.Feature(r => r.Plant.Length));
        Assert.Throws<ArgumentException>(() => builder.Feature(r => r.Plant.ToUpper()));
    }

    [Fact]
    public void A_property_cannot_be_both_an_input_and_the_answer_and_there_is_one_answer()
    {
        var builder = new MlDatasetBuilder<MlReading>().Feature(r => r.Temperature).Label(r => r.Kind);

        Assert.Throws<ArgumentException>(() => builder.Label(r => r.Temperature));
        Assert.Throws<ArgumentException>(() => builder.Feature(r => r.Kind));
        Assert.Throws<ArgumentException>(() => builder.Label(r => r.Plant));
    }

    [Fact]
    public void A_dataset_needs_an_input_a_name_and_a_name_not_taken()
    {
        Assert.Throws<ArgumentException>(() => new MlEntityFrameworkBuilder<MlTestDb>().Dataset<MlReading>("d", d => d.Label(r => r.Kind)));
        Assert.Throws<ArgumentException>(() => new MlEntityFrameworkBuilder<MlTestDb>().Dataset<MlReading>(" ", d => d.Feature(r => r.Temperature)));
        Assert.Throws<ArgumentException>(() => new MlEntityFrameworkBuilder<MlTestDb>()
            .Dataset<MlReading>("d", d => d.Feature(r => r.Temperature))
            .Dataset<MlReading>("D", d => d.Feature(r => r.Temperature)));
    }

    [Fact]
    public void A_second_source_and_an_empty_declaration_are_refused()
    {
        var services = new ServiceCollection();
        services.AddIyuMlEntityFramework<MlTestDb>(ef => ef.Dataset<MlReading>("d", d => d.Feature(r => r.Temperature)));

        Assert.Throws<InvalidOperationException>(() => services.AddIyuMlEntityFramework<MlTestDb>(ef => ef.Dataset<MlReading>("e", d => d.Feature(r => r.Temperature))));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddIyuMlEntityFramework<MlTestDb>(_ => { }));
    }

    [Fact]
    public async Task The_declared_datasets_reach_the_options_the_neutral_layer_checks()
    {
        var builder = Host();
        Declare(builder);
        builder.Services.AddIyuMl(o => o.NoReadPolicies());
        using var app = builder.Build();
        await app.StartAsync();

        Assert.Equal(["readings"], app.Services.GetRequiredService<IOptions<IyuMlOptions>>().Value.Datasets.Select(d => d.Name));
        await app.StopAsync();
    }

    [Fact]
    public void The_adapter_references_Entity_Framework_and_the_neutral_layer_and_nothing_of_the_host()
    {
        var adapter = typeof(EfMlDatasetSource<>).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.Contains("Iyu.Ml", adapter);
        Assert.Contains(adapter, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(adapter, n => n is "Iyu.Core" or "Iyu.MainServer" || n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
