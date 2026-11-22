using Iyu.UBoard;
using Iyu.UBoard.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Iyu.Tests.UBoard;

/// <summary>An entity of the application's own, to show the board tables sit beside it.</summary>
public class ShippedTablesNote
{
    public int Id { get; set; }

    public string Text { get; set; } = "";
}

/// <summary>A context the host writes: it asks for the shipped tables in <c>OnModelCreating</c>.</summary>
public class ShippedTablesOwnDb(DbContextOptions<ShippedTablesOwnDb> options) : DbContext(options)
{
    public DbSet<ShippedTablesNote> Notes => Set<ShippedTablesNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyIyuBoardTables();
}

/// <summary>A context nobody edits (a generated one): it knows nothing of boards.</summary>
public class ShippedTablesGeneratedDb(DbContextOptions<ShippedTablesGeneratedDb> options) : DbContext(options)
{
    public DbSet<ShippedTablesNote> Notes => Set<ShippedTablesNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<ShippedTablesNote>().ToTable("Notes");
}

/// <summary>
/// The package ships the three rows and their mapping, so a host does not write them: <c>ApplyIyuBoardTables</c> from its own context,
/// or <c>UseIyuBoardTables</c> on the options of one it cannot edit, then <c>AddIyuUBoard&lt;TContext&gt;()</c>.
/// </summary>
public sealed class ShippedBoardTablesTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ShippedBoardTablesTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private ServiceProvider StartOwn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ShippedTablesOwnDb>(o => o.UseSqlite(_connection));
        services.AddSingleton<IBoardAccess, EveryoneSignedInBoardAccess>();
        services.AddIyuUBoard<ShippedTablesOwnDb>();
        return Created<ShippedTablesOwnDb>(services);
    }

    private ServiceProvider StartGenerated()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ShippedTablesGeneratedDb>(o => o.UseSqlite(_connection).UseIyuBoardTables());
        services.AddSingleton<IBoardAccess, EveryoneSignedInBoardAccess>();
        services.AddIyuUBoard<ShippedTablesGeneratedDb>();
        return Created<ShippedTablesGeneratedDb>(services);
    }

    private static ServiceProvider Created<TContext>(IServiceCollection services) where TContext : DbContext
    {
        var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreated();
        return provider;
    }

    private static async Task Lifecycle(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var boards = scope.ServiceProvider.GetRequiredService<IyuBoardService>();

        var made = await boards.CreateAsync("sales", "Sales", """{"v":1}""", "ada");
        var saved = await boards.SaveAsync("sales", """{"v":2}""", 1, "ada");
        var stale = await boards.SaveAsync("sales", """{"v":3}""", 1, "grace");
        await boards.PublishAsync("sales", 2, "ada");

        Assert.True(made.Succeeded);
        Assert.True(saved.Succeeded);
        Assert.Equal(BoardChangeStatus.Conflict, stale.Status);
        Assert.Equal(2, (await boards.GetAsync("sales"))!.PublishedRevision);
        Assert.Equal(2, (await boards.ListRevisionsAsync("sales")).Count);
        Assert.Single(await boards.ListPublicationsAsync("sales"));
    }

    [Fact]
    public async Task A_context_that_applies_the_tables_keeps_boards_through_the_service_with_no_row_types_of_the_hosts()
    {
        using var provider = StartOwn();

        await Lifecycle(provider);
    }

    [Fact]
    public async Task A_context_nobody_edits_gets_the_tables_from_the_options_alone()
    {
        using var provider = StartGenerated();

        await Lifecycle(provider);
    }

    [Fact]
    public void Without_UseIyuBoardTables_a_context_that_knows_nothing_of_boards_does_not_start()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ShippedTablesGeneratedDb>(o => o.UseSqlite(_connection));
        services.AddSingleton<IBoardAccess, EveryoneSignedInBoardAccess>();
        services.AddIyuUBoard<ShippedTablesGeneratedDb>();
        using var provider = services.BuildServiceProvider();

        var failure = Assert.ThrowsAny<Exception>(() => provider.GetServices<IHostedService>().ToList());

        Assert.Contains("IyuBoardRow", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_model_has_the_three_tables_with_the_keys_the_store_relies_on_and_the_hosts_own_table_beside_them()
    {
        using var provider = StartGenerated();
        using var scope = provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<ShippedTablesGeneratedDb>().Model;

        Assert.Equal(["Id"], model.FindEntityType(typeof(IyuBoardRow))!.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(["BoardId", "Revision"], model.FindEntityType(typeof(IyuBoardRevisionRow))!.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(["BoardId", "Sequence"], model.FindEntityType(typeof(IyuBoardPublicationRow))!.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal("Notes", model.FindEntityType(typeof(ShippedTablesNote))!.GetTableName());
    }

    [Fact]
    public void The_tables_are_named_for_the_layer_and_the_text_columns_are_as_long_as_the_service_allows()
    {
        using var provider = StartOwn();
        using var scope = provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<ShippedTablesOwnDb>().Model;

        Assert.Equal("IyuBoards", model.FindEntityType(typeof(IyuBoardRow))!.GetTableName());
        Assert.Equal("IyuBoardRevisions", model.FindEntityType(typeof(IyuBoardRevisionRow))!.GetTableName());
        Assert.Equal("IyuBoardPublications", model.FindEntityType(typeof(IyuBoardPublicationRow))!.GetTableName());
        Assert.Equal(IyuBoardService.MaxTextLength, model.FindEntityType(typeof(IyuBoardRow))!.FindProperty("Id")!.GetMaxLength());
        Assert.Equal(IyuBoardService.MaxTextLength, model.FindEntityType(typeof(IyuBoardRevisionRow))!.FindProperty("CreatedBy")!.GetMaxLength());
    }

    [Fact]
    public async Task The_hosts_own_entities_are_still_stored_when_the_options_add_the_tables()
    {
        using var provider = StartGenerated();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShippedTablesGeneratedDb>();

        db.Notes.Add(new ShippedTablesNote { Text = "kept" });
        await db.SaveChangesAsync();

        Assert.Equal("kept", (await db.Notes.SingleAsync()).Text);
    }

    [Fact]
    public async Task Asking_for_the_tables_twice_changes_nothing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ShippedTablesOwnDb>(o => o.UseSqlite(_connection).UseIyuBoardTables());
        services.AddSingleton<IBoardAccess, EveryoneSignedInBoardAccess>();
        services.AddIyuUBoard<ShippedTablesOwnDb>();
        using var provider = Created<ShippedTablesOwnDb>(services);

        await Lifecycle(provider);
    }
}
