using Iyu.UBoard;
using Iyu.UBoard.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.UBoard;

public class UBoardRow : IBoardRow
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Document { get; set; } = "";

    public int CurrentRevision { get; set; }

    public int? PublishedRevision { get; set; }
}

public class UBoardRevisionRow : IBoardRevisionRow
{
    public string BoardId { get; set; } = "";

    public int Revision { get; set; }

    public string Document { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "";
}

public class UBoardPublicationRow : IBoardPublicationRow
{
    public string BoardId { get; set; } = "";

    public int Sequence { get; set; }

    public int? Revision { get; set; }

    public DateTimeOffset PublishedAt { get; set; }

    public string PublishedBy { get; set; } = "";
}

public class UBoardTestDb(DbContextOptions<UBoardTestDb> options) : DbContext(options)
{
    public DbSet<UBoardRow> Boards => Set<UBoardRow>();

    public DbSet<UBoardRevisionRow> Revisions => Set<UBoardRevisionRow>();

    public DbSet<UBoardPublicationRow> Publications => Set<UBoardPublicationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UBoardRow>().HasKey(b => b.Id);
        modelBuilder.Entity<UBoardRevisionRow>().HasKey(r => new { r.BoardId, r.Revision });
        modelBuilder.Entity<UBoardPublicationRow>().HasKey(p => new { p.BoardId, p.Sequence });
    }
}

/// <summary>A context that maps none of the board entities.</summary>
public class UBoardBareDb(DbContextOptions<UBoardBareDb> options) : DbContext(options)
{
    public DbSet<UBoardOtherRow> Others => Set<UBoardOtherRow>();
}

public class UBoardOtherRow
{
    public int Id { get; set; }
}

/// <summary>A context that maps the board entities with the revision key the wrong way round.</summary>
public class UBoardWrongKeyDb(DbContextOptions<UBoardWrongKeyDb> options) : DbContext(options)
{
    public DbSet<UBoardRow> Boards => Set<UBoardRow>();

    public DbSet<UBoardRevisionRow> Revisions => Set<UBoardRevisionRow>();

    public DbSet<UBoardPublicationRow> Publications => Set<UBoardPublicationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UBoardRow>().HasKey(b => b.Id);
        modelBuilder.Entity<UBoardRevisionRow>().HasKey(r => new { r.Revision, r.BoardId });
        modelBuilder.Entity<UBoardPublicationRow>().HasKey(p => new { p.BoardId, p.Sequence });
    }
}

/// <summary>A revision row keyed on a surrogate column of its own, with the (board, revision) pair unique — the shape a generated schema takes.</summary>
public class UBoardSurrogateRevisionRow : IBoardRevisionRow
{
    public int Id { get; set; }

    public string BoardId { get; set; } = "";

    public int Revision { get; set; }

    public string Document { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "";

    /// <summary>A column of the application's own: nullable, so a row the store makes without it is still valid.</summary>
    public string? ReadPermission { get; set; }
}

public class UBoardSurrogatePublicationRow : IBoardPublicationRow
{
    public int Id { get; set; }

    public string BoardId { get; set; } = "";

    public int Sequence { get; set; }

    public int? Revision { get; set; }

    public DateTimeOffset PublishedAt { get; set; }

    public string PublishedBy { get; set; } = "";
}

/// <summary>A context whose revision and publication tables are keyed on a surrogate, the pair carried by a unique index.</summary>
public class UBoardSurrogateKeyDb(DbContextOptions<UBoardSurrogateKeyDb> options) : DbContext(options)
{
    public DbSet<UBoardRow> Boards => Set<UBoardRow>();

    public DbSet<UBoardSurrogateRevisionRow> Revisions => Set<UBoardSurrogateRevisionRow>();

    public DbSet<UBoardSurrogatePublicationRow> Publications => Set<UBoardSurrogatePublicationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UBoardRow>().HasKey(b => b.Id);
        modelBuilder.Entity<UBoardSurrogateRevisionRow>().HasKey(r => r.Id);
        modelBuilder.Entity<UBoardSurrogateRevisionRow>().HasIndex(r => new { r.BoardId, r.Revision }).IsUnique();
        modelBuilder.Entity<UBoardSurrogatePublicationRow>().HasKey(p => p.Id);
        modelBuilder.Entity<UBoardSurrogatePublicationRow>().HasIndex(p => new { p.BoardId, p.Sequence }).IsUnique();
    }
}

/// <summary>A context whose revision and publication tables carry a foreign key to the board, with no cascade — what a schema made by hand often has.</summary>
public class UBoardForeignKeyDb(DbContextOptions<UBoardForeignKeyDb> options) : DbContext(options)
{
    public DbSet<UBoardRow> Boards => Set<UBoardRow>();

    public DbSet<UBoardRevisionRow> Revisions => Set<UBoardRevisionRow>();

    public DbSet<UBoardPublicationRow> Publications => Set<UBoardPublicationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UBoardRow>().HasKey(b => b.Id);
        modelBuilder.Entity<UBoardRevisionRow>().HasKey(r => new { r.BoardId, r.Revision });
        modelBuilder.Entity<UBoardRevisionRow>().HasOne<UBoardRow>().WithMany().HasForeignKey(r => r.BoardId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<UBoardPublicationRow>().HasKey(p => new { p.BoardId, p.Sequence });
        modelBuilder.Entity<UBoardPublicationRow>().HasOne<UBoardRow>().WithMany().HasForeignKey(p => p.BoardId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Like <see cref="UBoardSurrogateKeyDb"/> but the pair is not unique, so nothing stops two saves on one revision.</summary>
public class UBoardNonUniquePairDb(DbContextOptions<UBoardNonUniquePairDb> options) : DbContext(options)
{
    public DbSet<UBoardRow> Boards => Set<UBoardRow>();

    public DbSet<UBoardSurrogateRevisionRow> Revisions => Set<UBoardSurrogateRevisionRow>();

    public DbSet<UBoardSurrogatePublicationRow> Publications => Set<UBoardSurrogatePublicationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UBoardRow>().HasKey(b => b.Id);
        modelBuilder.Entity<UBoardSurrogateRevisionRow>().HasKey(r => r.Id);
        modelBuilder.Entity<UBoardSurrogateRevisionRow>().HasIndex(r => new { r.BoardId, r.Revision });
        modelBuilder.Entity<UBoardSurrogatePublicationRow>().HasKey(p => p.Id);
        modelBuilder.Entity<UBoardSurrogatePublicationRow>().HasIndex(p => new { p.BoardId, p.Sequence }).IsUnique();
    }
}

/// <summary>An execution strategy that claims to retry, as the ones for SQL Server and PostgreSQL do, so a user transaction must go through it.</summary>
internal sealed class RetryingTestStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, 3, TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception) => false;
}

/// <summary>The board store over the application's own tables, on SQLite.</summary>
public sealed class EFBoardStoreTests : EFBoardStoreContract
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-uboard-{Guid.NewGuid():N}.db");

    protected override void UseDatabase(DbContextOptionsBuilder options) => options.UseSqlite($"Data Source={_file}");

    protected override void DropDatabase()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }
}

/// <summary>The same facts on a context whose execution strategy retries, which refuses a transaction that was not begun through it.</summary>
public sealed class EFBoardStoreRetryingStrategyTests : EFBoardStoreContract
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-uboard-{Guid.NewGuid():N}.db");

    protected override void UseDatabase(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={_file}", sqlite => sqlite.ExecutionStrategy(d => new RetryingTestStrategy(d)));

    protected override void DropDatabase()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }
}

/// <summary>
/// What the store must do against a real database, written once for the engine to be chosen: a subclass says how to reach an empty
/// database and how to drop it, and the same facts are then held on each engine.
/// </summary>
public abstract class EFBoardStoreContract : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));

    /// <summary>Points the options at a database of this test's own, empty and not yet created.</summary>
    protected abstract void UseDatabase(DbContextOptionsBuilder options);

    /// <summary>Removes that database.</summary>
    protected abstract void DropDatabase();

    public void Dispose() => DropDatabase();

    private ServiceProvider Start()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<UBoardTestDb>(UseDatabase);
        services.AddIyuUBoard<UBoardTestDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>();
        var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<UBoardTestDb>().Database.EnsureCreated();
        return provider;
    }

    private static async Task<T> InScope<T>(ServiceProvider provider, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = provider.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private static Task<T> Board<T>(ServiceProvider provider, Func<IyuBoardService, Task<T>> work) =>
        InScope(provider, sp => work(sp.GetRequiredService<IyuBoardService>()));

    private static string Doc(string text) => $$"""{"title":"{{text}}"}""";

    [Fact]
    public async Task Boards_are_listed_by_id_a_page_at_a_time_with_where_they_stand()
    {
        using var provider = Start();
        foreach (var id in new[] { "c", "a", "b" }) await Board(provider, s => s.CreateAsync(id, "Board " + id, Doc(id), "ana"));
        await Board(provider, s => s.SaveAsync("b", Doc("b2"), 1, "bo"));
        await Board(provider, s => s.PublishAsync("b", 2, "bo"));

        var first = await Board(provider, s => s.ListAsync(0, 2));
        var second = await Board(provider, s => s.ListAsync(2, 2));

        Assert.Equal(
            new[] { new BoardSummary("a", "Board a", 1, null), new BoardSummary("b", "Board b", 2, 2) }, first);
        Assert.Equal(new[] { new BoardSummary("c", "Board c", 1, null) }, second);
        Assert.Empty(await Board(provider, s => s.ListAsync(3, 10)));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 1001)]
    public async Task A_page_outside_the_limits_is_refused(int skip, int take)
    {
        using var provider = Start();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Board(provider, s => s.ListAsync(skip, take)));
    }

    [Fact]
    public async Task A_rename_changes_the_name_and_leaves_the_document_and_the_revisions_alone()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("b"), 1, "bo"));

        var renamed = await Board(provider, s => s.RenameAsync("plant", "Plant overview"));
        var missing = await Board(provider, s => s.RenameAsync("nowhere", "Anything"));

        Assert.Equal(new BoardRecord("plant", "Plant overview", Doc("b"), 2, null), renamed.Board);
        Assert.True(renamed.Succeeded);
        Assert.Equal(BoardChangeStatus.NotFound, missing.Status);
        Assert.Equal(2, (await Board(provider, s => s.ListRevisionsAsync("plant"))).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => Board(provider, s => s.RenameAsync("plant", "  ")));
    }

    [Fact]
    public async Task A_delete_removes_the_board_every_revision_and_the_publication_history_and_no_other_board()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("b"), 1, "bo"));
        await Board(provider, s => s.PublishAsync("plant", 2, "bo"));
        await Board(provider, s => s.WithdrawAsync("plant", "bo"));
        await Board(provider, s => s.CreateAsync("line", "Line", Doc("x"), "ana"));
        await Board(provider, s => s.PublishAsync("line", 1, "ana"));

        var deleted = await Board(provider, s => s.DeleteAsync("plant"));
        var again = await Board(provider, s => s.DeleteAsync("plant"));

        Assert.True(deleted);
        Assert.False(again);
        Assert.Null(await Board(provider, s => s.GetAsync("plant")));
        Assert.Empty(await Board(provider, s => s.ListRevisionsAsync("plant")));
        Assert.Empty(await Board(provider, s => s.ListPublicationsAsync("plant")));
        Assert.NotNull(await Board(provider, s => s.GetAsync("line")));
        Assert.Single(await Board(provider, s => s.ListRevisionsAsync("line")));
        Assert.Single(await Board(provider, s => s.ListPublicationsAsync("line")));
    }

    // A delete that starts together with saves and publications of the same board must not leave their rows behind with no board:
    // it either finishes before them (they find no board) or after them (it removes what they wrote). A racer that the engine
    // turns away — a lock timeout, a deadlock victim — is not what is asked here; the state at the end is. SQLite serializes its
    // writers, so this is only a smoke test there; the engines that lock rows are where it can fail.
    [Fact]
    public async Task A_delete_racing_with_saves_and_publications_leaves_no_rows_of_the_board_behind()
    {
        using var provider = Start();
        for (var round = 0; round < 12; round++)
        {
            var id = $"race{round}";
            await Board(provider, s => s.CreateAsync(id, "Race", Doc("a"), "ana"));
            var gate = new TaskCompletionSource();
            var racers = new List<Task>();
            async Task Racer(Func<IyuBoardService, Task> work)
            {
                await gate.Task;
                try { await Board(provider, async s => { await work(s); return 0; }); }
                catch (Exception) { /* turned away by the engine: only the state at the end is judged */ }
            }

            racers.Add(Task.Run(() => Racer(s => s.DeleteAsync(id))));
            for (var i = 0; i < 3; i++)
            {
                racers.Add(Task.Run(() => Racer(s => s.SaveAsync(id, Doc("b"), 1, "bo"))));
                racers.Add(Task.Run(() => Racer(s => s.PublishAsync(id, 1, "bo"))));
            }

            gate.SetResult();
            await Task.WhenAll(racers);

            if (await Board(provider, s => s.GetAsync(id)) is null)
            {
                Assert.Empty(await Board(provider, s => s.ListRevisionsAsync(id)));
                Assert.Empty(await Board(provider, s => s.ListPublicationsAsync(id)));
            }
        }
    }

    [Fact]
    public async Task A_board_deleted_can_be_made_again_from_revision_one()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("b"), 1, "bo"));
        await Board(provider, s => s.DeleteAsync("plant"));

        var again = await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("c"), "cy"));

        Assert.Equal(new BoardRecord("plant", "Plant", Doc("c"), 1, null), again.Board);
        Assert.Single(await Board(provider, s => s.ListRevisionsAsync("plant")));
    }

    [Fact]
    public async Task A_new_board_starts_at_revision_one_and_a_second_creation_of_the_same_id_changes_nothing()
    {
        using var provider = Start();

        var first = await Board(provider, s => s.CreateAsync("plant", "Plant overview", Doc("a"), "ana"));
        var again = await Board(provider, s => s.CreateAsync("plant", "Another name", Doc("b"), "bo"));

        Assert.True(first.Succeeded);
        Assert.Equal(new BoardRecord("plant", "Plant overview", Doc("a"), 1, null), first.Board);
        Assert.Equal(BoardChangeStatus.AlreadyExists, again.Status);
        Assert.Equal(first.Board, again.Board);
        var revisions = await Board(provider, s => s.ListRevisionsAsync("plant"));
        Assert.Single(revisions);
        Assert.Equal(new BoardRevisionRecord("plant", 1, Doc("a"), _time.GetUtcNow(), "ana"), revisions[0]);
    }

    [Fact]
    public async Task A_save_on_the_current_revision_adds_a_revision_and_moves_the_board()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));
        _time.Advance(TimeSpan.FromMinutes(5));

        var saved = await Board(provider, s => s.SaveAsync("plant", Doc("b"), 1, "bo"));

        Assert.True(saved.Succeeded);
        Assert.Equal(new BoardRecord("plant", "Plant", Doc("b"), 2, null), saved.Board);
        Assert.Equal(saved.Board, await Board(provider, s => s.GetAsync("plant")));
        var revisions = await Board(provider, s => s.ListRevisionsAsync("plant"));
        Assert.Equal([2, 1], revisions.Select(r => r.Revision).ToArray());
        Assert.Equal(Doc("a"), revisions[1].Document);
        Assert.Equal("bo", revisions[0].CreatedBy);
        Assert.Equal(_time.GetUtcNow(), revisions[0].CreatedAt);
    }

    [Fact]
    public async Task A_save_on_a_revision_the_board_has_moved_past_changes_nothing_and_says_where_the_board_is()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("b"), 1, "ana"));

        var stale = await Board(provider, s => s.SaveAsync("plant", Doc("c"), 1, "bo"));

        Assert.Equal(BoardChangeStatus.Conflict, stale.Status);
        Assert.Equal(2, stale.Board!.CurrentRevision);
        Assert.Equal(Doc("b"), stale.Board.Document);
        Assert.Equal(2, (await Board(provider, s => s.ListRevisionsAsync("plant"))).Count);
    }

    [Fact]
    public async Task A_save_to_a_board_that_does_not_exist_is_not_found()
    {
        using var provider = Start();

        var change = await Board(provider, s => s.SaveAsync("missing", Doc("a"), 1, "ana"));

        Assert.Equal(BoardChangeStatus.NotFound, change.Status);
        Assert.Null(change.Board);
        Assert.Null(await Board(provider, s => s.GetAsync("missing")));
    }

    [Fact]
    public async Task Saves_made_together_on_one_revision_succeed_once_and_the_rest_are_refused()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Board(provider, s => s.SaveAsync("plant", Doc($"writer-{i}"), 1, $"user-{i}"))));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(7, results.Count(r => r.Status == BoardChangeStatus.Conflict));
        var board = await Board(provider, s => s.GetAsync("plant"));
        var revisions = await Board(provider, s => s.ListRevisionsAsync("plant"));
        Assert.Equal(2, board!.CurrentRevision);
        Assert.Equal([2, 1], revisions.Select(r => r.Revision).ToArray());
        Assert.Equal(board.Document, revisions[0].Document);
    }

    [Fact]
    public async Task A_save_made_inside_the_hosts_transaction_is_undone_with_it()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));

        await InScope(provider, async sp =>
        {
            // A host on a retrying strategy begins its own transaction through that strategy too; the store joins it either way.
            var db = sp.GetRequiredService<UBoardTestDb>();
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                var saved = await sp.GetRequiredService<IyuBoardService>().SaveAsync("plant", Doc("b"), 1, "ana");
                Assert.True(saved.Succeeded);
                await transaction.RollbackAsync();
            });
            return 0;
        });

        var board = await Board(provider, s => s.GetAsync("plant"));
        Assert.Equal(1, board!.CurrentRevision);
        Assert.Equal(Doc("a"), board.Document);
        Assert.Single(await Board(provider, s => s.ListRevisionsAsync("plant")));
    }

    [Fact]
    public async Task Publishing_a_revision_points_the_board_at_it_and_adds_a_line_to_the_history()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("b"), 1, "ana"));

        var line = await Board(provider, s => s.PublishAsync("plant", 1, "bo"));

        Assert.Equal(new BoardPublicationRecord("plant", 1, 1, _time.GetUtcNow(), "bo"), line);
        var board = await Board(provider, s => s.GetAsync("plant"));
        Assert.Equal(1, board!.PublishedRevision);
        Assert.Equal(2, board.CurrentRevision);
        Assert.Equal(Doc("a"), (await Board(provider, s => s.GetPublishedAsync("plant")))!.Document);
    }

    [Fact]
    public async Task Publishing_a_revision_that_does_not_exist_or_on_a_board_that_does_not_exist_changes_nothing()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));

        Assert.Null(await Board(provider, s => s.PublishAsync("plant", 5, "bo")));
        Assert.Null(await Board(provider, s => s.PublishAsync("missing", 1, "bo")));
        Assert.Null(await Board(provider, s => s.WithdrawAsync("missing", "bo")));

        Assert.Empty(await Board(provider, s => s.ListPublicationsAsync("plant")));
        Assert.Null((await Board(provider, s => s.GetAsync("plant")))!.PublishedRevision);
        Assert.Null(await Board(provider, s => s.GetPublishedAsync("plant")));
    }

    [Fact]
    public async Task Withdrawing_clears_the_pointer_and_publishing_an_older_revision_is_the_rollback()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("b"), 1, "ana"));
        await Board(provider, s => s.PublishAsync("plant", 1, "ana"));
        await Board(provider, s => s.PublishAsync("plant", 2, "ana"));

        var withdrawn = await Board(provider, s => s.WithdrawAsync("plant", "bo"));
        Assert.Null(withdrawn!.Revision);
        Assert.Null((await Board(provider, s => s.GetAsync("plant")))!.PublishedRevision);

        await Board(provider, s => s.PublishAsync("plant", 2, "ana"));
        await Board(provider, s => s.PublishAsync("plant", 1, "ana"));

        Assert.Equal(1, (await Board(provider, s => s.GetAsync("plant")))!.PublishedRevision);
        var history = await Board(provider, s => s.ListPublicationsAsync("plant"));
        Assert.Equal([5, 4, 3, 2, 1], history.Select(h => h.Sequence).ToArray());
        Assert.Equal([1, 2, null, 2, 1], history.Select(h => h.Revision).Reverse().ToArray());
        Assert.Equal(2, (await Board(provider, s => s.ListRevisionsAsync("plant"))).Count);
    }

    [Fact]
    public async Task Publications_made_together_each_take_their_own_place_in_the_history_and_the_pointer_follows_the_last()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("a"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("b"), 1, "ana"));

        var lines = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Board(provider, s => s.PublishAsync("plant", 1 + i % 2, $"user-{i}"))));

        Assert.All(lines, Assert.NotNull);
        var history = await Board(provider, s => s.ListPublicationsAsync("plant"));
        Assert.Equal([6, 5, 4, 3, 2, 1], history.Select(h => h.Sequence).ToArray());
        Assert.Equal(history[0].Revision, (await Board(provider, s => s.GetAsync("plant")))!.PublishedRevision);
    }

    [Fact]
    public async Task The_last_accepted_publication_comes_from_the_history_and_never_from_a_draft_with_a_lower_number()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("good-1"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("draft-2"), 1, "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("draft-3"), 2, "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("bad-4"), 3, "ana"));
        await Board(provider, s => s.PublishAsync("plant", 1, "ana"));
        await Board(provider, s => s.PublishAsync("plant", 4, "ana"));

        var found = await Board(provider, s => s.FindLastAcceptedPublicationAsync("plant", document => !document.Contains("bad")));

        Assert.Equal(1, found!.Revision);
        Assert.Equal(Doc("good-1"), found.Document);
    }

    [Fact]
    public async Task The_earlier_publications_are_listed_newest_first_each_once_without_the_one_in_force_a_withdrawal_or_a_draft()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("r1"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("r2"), 1, "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("draft-3"), 2, "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("r4"), 3, "ana"));

        Assert.Empty(await Board(provider, s => s.ListEarlierPublicationsAsync("plant")));

        await Board(provider, s => s.PublishAsync("plant", 1, "ana"));
        Assert.Empty(await Board(provider, s => s.ListEarlierPublicationsAsync("plant")));
        await Board(provider, s => s.PublishAsync("plant", 2, "ana"));
        await Board(provider, s => s.WithdrawAsync("plant", "ana"));
        await Board(provider, s => s.PublishAsync("plant", 1, "ana"));
        await Board(provider, s => s.PublishAsync("plant", 4, "ana"));

        var earlier = await Board(provider, s => s.ListEarlierPublicationsAsync("plant"));

        Assert.Equal([1, 2], earlier.Select(r => r.Revision).ToArray());
        Assert.Equal([Doc("r1"), Doc("r2")], earlier.Select(r => r.Document).ToArray());
        Assert.Empty(await Board(provider, s => s.ListEarlierPublicationsAsync("missing")));
    }

    [Fact]
    public async Task No_accepted_publication_is_found_for_a_board_that_is_not_published_or_has_nothing_earlier_to_fall_back_to()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("bad-1"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("bad-2"), 1, "ana"));

        Assert.Null(await Board(provider, s => s.FindLastAcceptedPublicationAsync("plant", _ => true)));

        await Board(provider, s => s.PublishAsync("plant", 1, "ana"));
        await Board(provider, s => s.PublishAsync("plant", 2, "ana"));
        Assert.Null(await Board(provider, s => s.FindLastAcceptedPublicationAsync("plant", d => !d.Contains("bad"))));

        await Board(provider, s => s.WithdrawAsync("plant", "ana"));
        Assert.Null(await Board(provider, s => s.FindLastAcceptedPublicationAsync("plant", _ => true)));
    }

    [Fact]
    public async Task A_fallback_skips_a_revision_that_was_published_more_than_once_and_a_withdrawal()
    {
        using var provider = Start();
        await Board(provider, s => s.CreateAsync("plant", "Plant", Doc("good-1"), "ana"));
        await Board(provider, s => s.SaveAsync("plant", Doc("bad-2"), 1, "ana"));
        await Board(provider, s => s.PublishAsync("plant", 1, "ana"));
        await Board(provider, s => s.WithdrawAsync("plant", "ana"));
        await Board(provider, s => s.PublishAsync("plant", 2, "ana"));
        await Board(provider, s => s.PublishAsync("plant", 2, "ana"));

        var found = await Board(provider, s => s.FindLastAcceptedPublicationAsync("plant", d => !d.Contains("bad")));

        Assert.Equal(1, found!.Revision);
    }

    [Fact]
    public async Task A_context_that_does_not_map_the_tables_stops_the_host_and_says_which()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<UBoardBareDb>(UseDatabase);
        services.AddIyuUBoard<UBoardBareDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>();
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<InvalidOperationException>(() => provider.GetServices<IHostedService>().ToList());

        Assert.Contains("UBoardRow in UBoardBareDb", failure.Message);
        Assert.Contains("UBoardRevisionRow in UBoardBareDb", failure.Message);
        Assert.Contains("UBoardPublicationRow in UBoardBareDb", failure.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_revision_table_whose_key_is_in_the_wrong_order_stops_the_host()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<UBoardWrongKeyDb>(UseDatabase);
        services.AddIyuUBoard<UBoardWrongKeyDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>();
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<InvalidOperationException>(() => provider.GetServices<IHostedService>().ToList());

        Assert.Contains("UBoardRevisionRow", failure.Message);
        Assert.Contains("(BoardId, Revision)", failure.Message);
        Assert.DoesNotContain("UBoardPublicationRow", failure.Message);
        await Task.CompletedTask;
    }
}

/// <summary>
/// The store's one demand on the application's tables is that the (board, revision) and (board, sequence) pairs are unique, in that
/// order. A schema keyed on a surrogate with the pair as a unique index meets it, and the saves are decided as they are on the
/// composite key.
/// </summary>
public sealed class EFBoardStoreSurrogateKeyTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-uboard-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private ServiceProvider Start<TContext>()
        where TContext : DbContext
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TContext>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddIyuUBoard<TContext, UBoardRow, UBoardSurrogateRevisionRow, UBoardSurrogatePublicationRow>();
        var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreated();
        return provider;
    }

    private static async Task<T> Board<T>(ServiceProvider provider, Func<IyuBoardService, Task<T>> work)
    {
        using var scope = provider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IyuBoardService>());
    }

    [Fact]
    public async Task A_surrogate_key_with_the_pair_unique_starts_the_host_and_saves_as_on_the_composite_key()
    {
        using var provider = Start<UBoardSurrogateKeyDb>();
        provider.GetServices<IHostedService>().ToList();   // the model check runs here and does not throw

        await Board(provider, s => s.CreateAsync("plant", "Plant", """{"t":"a"}""", "ana"));
        var saved = await Board(provider, s => s.SaveAsync("plant", """{"t":"b"}""", 1, "bo"));
        var stale = await Board(provider, s => s.SaveAsync("plant", """{"t":"c"}""", 1, "cy"));
        var published = await Board(provider, s => s.PublishAsync("plant", 2, "bo"));
        var again = await Board(provider, s => s.WithdrawAsync("plant", "bo"));

        Assert.True(saved.Succeeded);
        Assert.Equal(BoardChangeStatus.Conflict, stale.Status);
        Assert.Equal(2, (await Board(provider, s => s.ListRevisionsAsync("plant"))).Count);
        Assert.NotNull(published);
        Assert.NotNull(again);
        Assert.Equal(2, (await Board(provider, s => s.ListPublicationsAsync("plant"))).Count);
    }

    [Fact]
    public async Task A_pair_that_is_not_unique_stops_the_host_naming_the_table()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<UBoardNonUniquePairDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddIyuUBoard<UBoardNonUniquePairDb, UBoardRow, UBoardSurrogateRevisionRow, UBoardSurrogatePublicationRow>();
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<InvalidOperationException>(() => provider.GetServices<IHostedService>().ToList());

        Assert.Contains("UBoardSurrogateRevisionRow", failure.Message);
        Assert.Contains("(BoardId, Revision)", failure.Message);
        Assert.DoesNotContain("UBoardSurrogatePublicationRow", failure.Message);
        await Task.CompletedTask;
    }
}

/// <summary>A delete on tables that carry a foreign key from the children to the board: the children have to go before the board.</summary>
public sealed class EFBoardStoreForeignKeyTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-uboard-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    [Fact]
    public async Task A_delete_succeeds_where_the_revisions_and_the_history_hold_a_foreign_key_to_the_board()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<UBoardForeignKeyDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddIyuUBoard<UBoardForeignKeyDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<UBoardForeignKeyDb>().Database.EnsureCreated();

        async Task<T> Run<T>(Func<IyuBoardService, Task<T>> work)
        {
            using var scope = provider.CreateScope();
            return await work(scope.ServiceProvider.GetRequiredService<IyuBoardService>());
        }

        await Run(s => s.CreateAsync("plant", "Plant", """{"t":"a"}""", "ana"));
        await Run(s => s.SaveAsync("plant", """{"t":"b"}""", 1, "bo"));
        await Run(s => s.PublishAsync("plant", 2, "bo"));

        var deleted = await Run(s => s.DeleteAsync("plant"));

        Assert.True(deleted);
        Assert.Null(await Run(s => s.GetAsync("plant")));
        Assert.Empty(await Run(s => s.ListRevisionsAsync("plant")));
        Assert.Empty(await Run(s => s.ListPublicationsAsync("plant")));
    }
}
