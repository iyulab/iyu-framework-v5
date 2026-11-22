using System.Net;
using System.Security.Claims;
using System.Text;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.UBoard;

/// <summary>
/// Who may do what to which board is the application's to say, and the layer asks and enforces: a board the caller may not view is
/// not there for them, a thing they may not do is refused before it is done, a list holds only what they may open, and a host that
/// says nothing about it does not start.
/// </summary>
public sealed class BoardAccessTests
{
    private const string Doc = """{"title":"Plan","tiles":[]}""";

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

    /// <summary>
    /// A small permission model of an application: the board's owner may do everything to it, an editor may view and edit, a viewer
    /// may only view; anyone named in <c>Makers</c> may make boards. Everything asked is recorded.
    /// </summary>
    private sealed class Rules : IBoardAccess
    {
        public Dictionary<(string User, string Board), HashSet<BoardAction>> Grants { get; } = [];

        public HashSet<string> Makers { get; } = [];

        public List<(string User, BoardAction Action, string? Board)> Asked { get; } = [];

        public List<(string User, string Board)> Created { get; } = [];

        public void Grant(string user, string board, params BoardAction[] actions) => Grants[(user, board)] = [.. actions];

        public ValueTask<bool> AllowsAsync(ClaimsPrincipal user, BoardAction action, string? boardId, CancellationToken cancellationToken = default)
        {
            var name = user.Identity!.Name!;
            Asked.Add((name, action, boardId));
            if (action == BoardAction.Create) return ValueTask.FromResult(Makers.Contains(name));
            return ValueTask.FromResult(boardId is not null && Grants.TryGetValue((name, boardId), out var actions) && actions.Contains(action));
        }

        public ValueTask CreatedAsync(ClaimsPrincipal user, string boardId, CancellationToken cancellationToken = default)
        {
            var name = user.Identity!.Name!;
            Created.Add((name, boardId));
            Grant(name, boardId, BoardAction.View, BoardAction.Edit, BoardAction.Publish, BoardAction.Delete);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Host(WebApplication app, SqliteConnection connection, Rules rules) : IAsyncDisposable
    {
        public HttpClient Client { get; } = app.GetTestServer().CreateClient();

        public Rules Rules { get; } = rules;

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string user, string? body = null, string prefix = "/uboard/boards")
        {
            var request = new HttpRequestMessage(method, prefix + path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Test-User", user);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static async Task<Host> StartAsync(Rules rules)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UserHeaderHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<UBoardTestDb>(o => o.UseSqlite(connection));
        builder.Services.AddSingleton<IBoardAccess>(rules);
        builder.Services.AddIyuUBoard<UBoardTestDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>();

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<UBoardTestDb>().Database.EnsureCreated();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();
        return new Host(app, connection, rules);
    }

    /// <summary>Ann made "plan" (so owns it); Eve may edit it; Vic may only view it; Zed has nothing to do with it.</summary>
    private static async Task<Host> StartWithAPlanAsync()
    {
        var rules = new Rules();
        rules.Makers.Add("ann");
        var host = await StartAsync(rules);
        using var made = await host.SendAsync(HttpMethod.Post, "/plan?name=Plan", "ann", Doc);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        rules.Grant("eve", "plan", BoardAction.View, BoardAction.Edit);
        rules.Grant("vic", "plan", BoardAction.View);
        rules.Asked.Clear();
        rules.Created.Clear();
        return host;
    }

    private static async Task<JsonNode> Read(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task A_host_that_turns_the_boards_on_without_saying_who_may_do_what_does_not_start()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        builder.Services.AddDbContext<UBoardTestDb>(o => o.UseSqlite("Data Source=:memory:"));
        builder.Services.AddIyuUBoard<UBoardTestDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>();
        using var app = builder.Build();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("IBoardAccess", failure.Message);
        Assert.Contains("EveryoneSignedInBoardAccess", failure.Message);
    }

    [Fact]
    public async Task A_board_the_caller_may_not_view_is_not_there_for_them_on_every_route_and_looks_like_a_board_that_does_not_exist()
    {
        await using var host = await StartWithAPlanAsync();
        string[] reads = ["/plan", "/plan/revisions", "/plan/revisions/1", "/plan/publications", "/plan/published", "/plan/fallback"];

        foreach (var path in reads)
        {
            using var hidden = await host.SendAsync(HttpMethod.Get, path, "zed");
            using var absent = await host.SendAsync(HttpMethod.Get, path.Replace("plan", "ghost"), "ann");
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Equal(absent.StatusCode, hidden.StatusCode);
        }

        // Nor can they change it — and asking to is no way to learn that it exists.
        using var save = await host.SendAsync(HttpMethod.Put, "/plan?expected=1", "zed", Doc);
        using var rename = await host.SendAsync(HttpMethod.Put, "/plan/name?name=Mine", "zed");
        using var publish = await host.SendAsync(HttpMethod.Post, "/plan/publish?revision=1", "zed");
        using var delete = await host.SendAsync(HttpMethod.Delete, "/plan", "zed");
        Assert.All([save, rename, publish, delete], r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
    }

    [Fact]
    public async Task A_caller_who_may_view_but_not_edit_is_told_so_and_nothing_is_changed()
    {
        await using var host = await StartWithAPlanAsync();

        using var save = await host.SendAsync(HttpMethod.Put, "/plan?expected=1", "vic", Doc);
        using var rename = await host.SendAsync(HttpMethod.Put, "/plan/name?name=Mine", "vic");
        using var publish = await host.SendAsync(HttpMethod.Post, "/plan/publish?revision=1", "vic");
        using var withdraw = await host.SendAsync(HttpMethod.Post, "/plan/withdraw", "vic");
        using var delete = await host.SendAsync(HttpMethod.Delete, "/plan", "vic");

        Assert.All([save, rename, publish, withdraw, delete], r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));

        using var after = await host.SendAsync(HttpMethod.Get, "/plan", "ann");
        var board = await Read(after);
        Assert.Equal(1, (int?)board["revision"]);
        Assert.Equal("Plan", (string?)board["name"]);
        Assert.Null(board["publishedRevision"]);

        // Reading is theirs.
        using var read = await host.SendAsync(HttpMethod.Get, "/plan", "vic");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task An_editor_may_save_and_rename_but_not_publish_or_delete_when_the_application_says_so()
    {
        await using var host = await StartWithAPlanAsync();

        using var save = await host.SendAsync(HttpMethod.Put, "/plan?expected=1", "eve", """{"title":"Edited"}""");
        using var rename = await host.SendAsync(HttpMethod.Put, "/plan/name?name=Edited%20plan", "eve");
        using var publish = await host.SendAsync(HttpMethod.Post, "/plan/publish?revision=2", "eve");
        using var delete = await host.SendAsync(HttpMethod.Delete, "/plan", "eve");

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Each_route_asks_for_the_action_it_is_and_the_board_it_names()
    {
        await using var host = await StartWithAPlanAsync();
        host.Rules.Grant("ann", "plan", BoardAction.View, BoardAction.Edit, BoardAction.Publish, BoardAction.Delete);
        (HttpMethod Method, string Path, string? Body, BoardAction Asks)[] calls =
        [
            (HttpMethod.Get, "/plan", null, BoardAction.View),
            (HttpMethod.Get, "/plan/revisions", null, BoardAction.View),
            (HttpMethod.Get, "/plan/published", null, BoardAction.View),
            (HttpMethod.Put, "/plan?expected=1", Doc, BoardAction.Edit),
            (HttpMethod.Put, "/plan/name?name=Again", null, BoardAction.Edit),
            (HttpMethod.Post, "/plan/publish?revision=2", null, BoardAction.Publish),
            (HttpMethod.Post, "/plan/withdraw", null, BoardAction.Publish),
            (HttpMethod.Delete, "/plan", null, BoardAction.Delete),
        ];

        foreach (var (method, path, body, asks) in calls)
        {
            host.Rules.Asked.Clear();
            using var _ = await host.SendAsync(method, path, "ann", body);
            Assert.Contains(("ann", asks, "plan"), host.Rules.Asked);
        }
    }

    [Fact]
    public async Task Making_a_board_needs_the_application_to_say_yes_and_the_application_hears_about_the_new_board()
    {
        await using var host = await StartWithAPlanAsync();

        using var refused = await host.SendAsync(HttpMethod.Post, "/mine?name=Mine", "vic", Doc);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var absent = await host.SendAsync(HttpMethod.Get, "/mine", "ann");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);   // nothing was made
        Assert.Empty(host.Rules.Created);

        using var made = await host.SendAsync(HttpMethod.Post, "/mine?name=Mine", "ann", Doc);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal([("ann", "mine")], host.Rules.Created);
        Assert.True((bool?)(await Read(made))["can"]!["edit"]);   // the application's answer for the new board is already in

        // The id is taken: the application is not told of a second creation.
        host.Rules.Created.Clear();
        using var again = await host.SendAsync(HttpMethod.Post, "/mine?name=Again", "ann", Doc);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Empty(host.Rules.Created);
    }

    [Fact]
    public async Task A_caller_who_may_not_view_a_board_that_has_the_id_they_chose_learns_only_that_the_id_is_taken()
    {
        await using var host = await StartWithAPlanAsync();
        host.Rules.Makers.Add("zed");

        using var taken = await host.SendAsync(HttpMethod.Post, "/plan?name=Mine", "zed", Doc);
        var body = await taken.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.DoesNotContain("Plan", body);       // neither the name nor the document of someone else's board
        Assert.DoesNotContain("tiles", body);
    }

    [Fact]
    public async Task A_list_holds_only_the_boards_the_caller_may_open_and_says_what_they_may_do_to_each()
    {
        await using var host = await StartWithAPlanAsync();
        using var other = await host.SendAsync(HttpMethod.Post, "/private?name=Private", "ann", Doc);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);

        using var forVic = await host.SendAsync(HttpMethod.Get, "", "vic");
        var vics = (JsonArray)(await Read(forVic));
        Assert.Equal(["plan"], vics.Select(b => (string)b!["id"]!).ToArray());
        Assert.False((bool?)vics[0]!["can"]!["edit"]);
        Assert.False((bool?)vics[0]!["can"]!["delete"]);

        using var forAnn = await host.SendAsync(HttpMethod.Get, "", "ann");
        var anns = (JsonArray)(await Read(forAnn));
        Assert.Equal(["plan", "private"], anns.Select(b => (string)b!["id"]!).Order().ToArray());
        Assert.All(anns, b => Assert.True((bool?)b!["can"]!["edit"]));

        using var forZed = await host.SendAsync(HttpMethod.Get, "", "zed");
        Assert.Empty((JsonArray)(await Read(forZed)));
    }

    [Fact]
    public async Task A_board_read_says_whether_the_caller_may_edit_it_so_a_screen_can_show_it_read_only()
    {
        await using var host = await StartWithAPlanAsync();

        var eve = await Read(await host.SendAsync(HttpMethod.Get, "/plan", "eve"));
        var vic = await Read(await host.SendAsync(HttpMethod.Get, "/plan", "vic"));

        Assert.True((bool?)eve["can"]!["edit"]);
        Assert.False((bool?)eve["can"]!["publish"]);
        Assert.False((bool?)vic["can"]!["edit"]);
    }

    [Fact]
    public async Task The_access_route_tells_a_screen_whether_to_offer_a_new_board()
    {
        await using var host = await StartWithAPlanAsync();

        var ann = await Read(await host.SendAsync(HttpMethod.Get, "/access", "ann", prefix: "/uboard"));
        var vic = await Read(await host.SendAsync(HttpMethod.Get, "/access", "vic", prefix: "/uboard"));

        Assert.True((bool?)ann["create"]);
        Assert.False((bool?)vic["create"]);
        Assert.Contains(("vic", BoardAction.Create, (string?)null), host.Rules.Asked);
    }
}
