using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Iyu.UBoard;
using Iyu.UBoard.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.UBoard;

/// <summary>
/// The board list walks the store in order, hides what the caller may not view and stops at the page size, so a host that hides most
/// of its boards from a person still gets every visible board across the pages, once each. The page says where to go on in the
/// <c>X-UBoard-Next-Skip</c> header; a short page that carries it is not the end. One request looks at a bounded number of rows.
/// </summary>
public sealed class BoardListPagingTests
{
    private const string Doc = """{"title":"Plan","tiles":[]}""";
    private const string Next = "X-UBoard-Next-Skip";

    /// <summary>Who may view which board: "seer" every seventh board, "sparse" the first and the one at offset 1150, "blind" none, "all" every one. Counts the view questions.</summary>
    private sealed class Rules : IBoardAccess
    {
        private int _views;

        public int Views => _views;

        public void Reset() => _views = 0;

        public ValueTask<bool> AllowsAsync(ClaimsPrincipal user, BoardAction action, string? boardId, CancellationToken cancellationToken = default)
        {
            if (action == BoardAction.View) Interlocked.Increment(ref _views);
            if (action == BoardAction.Create) return ValueTask.FromResult(true);
            if (boardId is null) return ValueTask.FromResult(false);
            var n = int.Parse(boardId.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
            return ValueTask.FromResult(user.Identity!.Name switch
            {
                "all" => action == BoardAction.View,
                "seer" => action == BoardAction.View && n % 7 == 0,
                "sparse" => action == BoardAction.View && (n == 0 || n == 1150),
                _ => false,
            });
        }

        public ValueTask CreatedAsync(ClaimsPrincipal user, string boardId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class UserHeaderHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var name)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, name.ToString())], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class Host(WebApplication app, SqliteConnection connection, Rules rules) : IAsyncDisposable
    {
        public Rules Rules { get; } = rules;

        private HttpClient Client { get; } = app.GetTestServer().CreateClient();

        /// <summary>One page of the list: the ids it holds and the offset the answer says to go on from (null when it names none).</summary>
        public async Task<(string[] Ids, int? Next)> PageAsync(string user, int skip, int take)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/uboard/boards?skip={skip}&take={take}");
            request.Headers.Add("X-Test-User", user);
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var ids = ((JsonArray)JsonNode.Parse(await response.Content.ReadAsStringAsync())!).Select(b => (string)b!["id"]!).ToArray();
            int? next = response.Headers.TryGetValues(Next, out var values) ? int.Parse(values.Single(), System.Globalization.CultureInfo.InvariantCulture) : null;
            return (ids, next);
        }

        /// <summary>Every page a caller is told to read, each asked for where the one before said to go on.</summary>
        public async Task<List<(string[] Ids, int? Next)>> WalkAsync(string user, int take)
        {
            var pages = new List<(string[] Ids, int? Next)>();
            int? skip = 0;
            while (skip is { } at)
            {
                var page = await PageAsync(user, at, take);
                pages.Add(page);
                Assert.True(pages.Count < 200, "the walk does not end");
                skip = page.Next;
            }

            return pages;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>A host holding boards b0000 … b{count-1}, ordered by id as the store lists them.</summary>
    private static async Task<Host> StartAsync(int count)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var rules = new Rules();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UserHeaderHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<UBoardTestDb>(o => o.UseSqlite(connection));
        builder.Services.AddSingleton<IBoardAccess>(rules);
        builder.Services.AddIyuUBoard<UBoardTestDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>();

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<UBoardTestDb>().Database.EnsureCreated();
            var service = scope.ServiceProvider.GetRequiredService<IyuBoardService>();
            for (var n = 0; n < count; n++)
                Assert.True((await service.CreateAsync($"b{n:D4}", $"Board {n}", Doc, "seed")).Succeeded);
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();
        rules.Reset();
        return new Host(app, connection, rules);
    }

    private static string[] EveryBoard(int count, Func<int, bool> visible) =>
        Enumerable.Range(0, count).Where(visible).Select(n => $"b{n:D4}").ToArray();

    [Fact]
    public async Task A_person_who_may_view_every_seventh_board_gets_every_visible_board_once_across_full_pages()
    {
        await using var host = await StartAsync(120);

        var pages = await host.WalkAsync("seer", take: 3);

        Assert.Equal(EveryBoard(120, n => n % 7 == 0), pages.SelectMany(p => p.Ids).ToArray());
        Assert.All(pages, p => Assert.Equal(3, p.Ids.Length));
        Assert.Equal(6, pages.Count);
        Assert.Null(pages[^1].Next);
    }

    [Fact]
    public async Task The_continuation_is_the_offset_of_the_first_board_the_page_did_not_take()
    {
        await using var host = await StartAsync(120);

        var (ids, next) = await host.PageAsync("seer", skip: 0, take: 17);
        var (all, nothing) = await host.PageAsync("seer", skip: 0, take: 18);

        Assert.Equal(17, ids.Length);
        Assert.Equal(119, next);
        Assert.Equal(18, all.Length);
        Assert.Null(nothing);
        Assert.Equal(["b0119"], (await host.PageAsync("seer", 119, 17)).Ids);
    }

    [Fact]
    public async Task A_person_who_may_view_nothing_gets_an_empty_page_without_a_continuation_once_the_store_is_looked_through()
    {
        await using var host = await StartAsync(120);

        var (ids, next) = await host.PageAsync("blind", skip: 0, take: 5);

        Assert.Empty(ids);
        Assert.Null(next);
    }

    [Fact]
    public async Task One_request_looks_at_a_bounded_number_of_boards_and_says_where_to_go_on()
    {
        await using var host = await StartAsync(1200);
        host.Rules.Reset();

        var (ids, next) = await host.PageAsync("blind", skip: 0, take: 10);

        Assert.Empty(ids);
        Assert.Equal(1000, next);
        Assert.Equal(1000, host.Rules.Views);
        Assert.Equal((Array.Empty<string>(), (int?)null), await host.PageAsync("blind", skip: 1000, take: 10));
    }

    [Fact]
    public async Task A_short_page_that_names_a_continuation_is_not_the_end()
    {
        await using var host = await StartAsync(1200);

        var pages = await host.WalkAsync("sparse", take: 5);

        Assert.Equal(["b0000"], pages[0].Ids);
        Assert.Equal(1000, pages[0].Next);
        Assert.Equal(["b1150"], pages.SelectMany(p => p.Ids).Skip(1).ToArray());
        Assert.Null(pages[^1].Next);
    }

    [Fact]
    public async Task A_person_who_may_view_everything_reads_the_whole_store_in_pages_of_the_size_asked_for()
    {
        await using var host = await StartAsync(1200);

        var pages = await host.WalkAsync("all", take: 400);

        Assert.Equal(EveryBoard(1200, _ => true), pages.SelectMany(p => p.Ids).ToArray());
        Assert.Equal([400, 400, 400], pages.Select(p => p.Ids.Length));
    }
}
