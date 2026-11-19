using System.Reflection;
using System.Text.RegularExpressions;
using Iyu.UBoard;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Iyu.Tests.UBoard;

/// <summary>
/// The README's board snippet is what a new user copies first, so it is compiled and run here against the packages as they are —
/// read from the test output where <c>Iyu.Tests.csproj</c> copies the README, never from a copy kept in test code. The snippet
/// leaves out what a host brings itself (its context and row types); the harness below supplies exactly that and nothing else.
/// </summary>
public sealed class ReadmeSampleTests
{
    private const string Marker = "<!-- uboard-sample -->";
    private const string RequestComment = "// in a request:";

    private const string Harness = """
        using System;
        using System.Threading.Tasks;
        using Iyu.UBoard;
        using Iyu.UBoard.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        public class BoardRow : IBoardRow
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Document { get; set; } = "";
            public int CurrentRevision { get; set; }
            public int? PublishedRevision { get; set; }
        }

        public class BoardRevisionRow : IBoardRevisionRow
        {
            public string BoardId { get; set; } = "";
            public int Revision { get; set; }
            public string Document { get; set; } = "";
            public DateTimeOffset CreatedAt { get; set; }
            public string CreatedBy { get; set; } = "";
        }

        public class BoardPublicationRow : IBoardPublicationRow
        {
            public string BoardId { get; set; } = "";
            public int Sequence { get; set; }
            public int? Revision { get; set; }
            public DateTimeOffset PublishedAt { get; set; }
            public string PublishedBy { get; set; } = "";
        }

        public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
        {
            public DbSet<BoardRow> Boards => Set<BoardRow>();
            public DbSet<BoardRevisionRow> Revisions => Set<BoardRevisionRow>();
            public DbSet<BoardPublicationRow> Publications => Set<BoardPublicationRow>();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<BoardRow>().HasKey(b => b.Id);
                modelBuilder.Entity<BoardRevisionRow>().HasKey(r => new { r.BoardId, r.Revision });
                modelBuilder.Entity<BoardPublicationRow>().HasKey(p => new { p.BoardId, p.Sequence });
            }
        }

        public static class ReadmeSample
        {
            public static void Register(IHostApplicationBuilder builder)
            {
        {{REGISTER}}
            }

            public static async Task Request(IyuBoardService boards, string user, string firstDocumentJson, string editedJson)
            {
        {{REQUEST}}
            }
        }
        """;

    private static (string Register, string Request) Sample()
    {
        var match = Regex.Match(
            ReadmeCompilation.ReadmeText(),
            Regex.Escape(Marker) + @"\r?\n```csharp\r?\n(?<code>.*?)\r?\n```",
            RegexOptions.Singleline);
        Assert.True(match.Success,
            "the Iyu.UBoard sample was not found in the README (the code block that follows " + Marker + "). If the section "
            + "was reworded, keep the marker: this test asserts nothing while its subject is missing.");

        var code = match.Groups["code"].Value;
        var split = code.IndexOf(RequestComment, StringComparison.Ordinal);
        Assert.True(split > 0, $"the sample no longer separates registration from a request with '{RequestComment}'.");
        return (code[..split], code[(split + RequestComment.Length)..]);
    }

    private static string Source()
    {
        var (register, request) = Sample();
        return Harness.Replace("{{REGISTER}}", register).Replace("{{REQUEST}}", request);
    }

    [Fact]
    public void The_snippet_in_the_README_compiles_against_these_packages()
    {
        var errors = ReadmeCompilation.Errors("UBoardSample", Source())
            .Select(e => $"{e.Id}: {e.GetMessage()} ({e.Location.GetLineSpan().StartLinePosition.Line + 1})");

        Assert.Empty(errors);
    }

    /// <summary>
    /// Compiling proves the names exist, not that the calls do what the text beside them says, so the snippet runs: the host
    /// starts with it, and a request made as shown creates the board, saves a second revision, and publishes that one.
    /// </summary>
    [Fact]
    public async Task The_snippet_runs_a_board_from_first_save_to_publication()
    {
        var file = Path.Combine(Path.GetTempPath(), $"iyu-uboard-sample-{Guid.NewGuid():N}.db");
        var assembly = ReadmeCompilation.Load("UBoardSampleHost", Source());
        var context = assembly.GetType("AppDbContext")!;
        var sample = assembly.GetType("ReadmeSample")!;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });

        // What the host brings: its context, on its own database. The snippet adds the board layer on top of it.
        var addContext = typeof(EntityFrameworkServiceCollectionExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(EntityFrameworkServiceCollectionExtensions.AddDbContext)
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 4
                && m.GetParameters()[1].ParameterType == typeof(Action<DbContextOptionsBuilder>));
        addContext.MakeGenericMethod(context)
            .Invoke(null, [builder.Services, (Action<DbContextOptionsBuilder>)(o => o.UseSqlite($"Data Source={file}")), ServiceLifetime.Scoped, ServiceLifetime.Scoped]);
        sample.GetMethod("Register")!.Invoke(null, [builder]);

        using var app = builder.Build();
        try
        {
            await app.StartAsync();
            using var scope = app.Services.CreateScope();
            ((DbContext)scope.ServiceProvider.GetRequiredService(context)).Database.EnsureCreated();

            var boards = scope.ServiceProvider.GetRequiredService<IyuBoardService>();
            await (Task)sample.GetMethod("Request")!.Invoke(null, [boards, "alice", """{"v":1}""", """{"v":2}"""])!;

            var board = await boards.GetAsync("plant");
            Assert.NotNull(board);
            Assert.Equal("Plant overview", board.Name);
            Assert.Equal(2, board.CurrentRevision);
            Assert.Equal(2, board.PublishedRevision);
            Assert.Equal("""{"v":2}""", board.Document);
            Assert.Equal(2, (await boards.ListRevisionsAsync("plant")).Count);
        }
        finally
        {
            await app.StopAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public void The_snippet_names_the_conflict_it_describes()
    {
        // The text beside the snippet says a save made on a stale revision answers Conflict with the board as it stands.
        var (_, request) = Sample();

        Assert.Contains("BoardChangeStatus.Conflict", request);
        Assert.Contains("expectedRevision", request);
    }
}
