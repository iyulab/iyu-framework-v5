using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// The board screen the package serves: a page for the list and one per board id, the files they load, and the notices that travel with
/// them. Who may open a page is asked of the application like any board route; what a page may load and do is fixed by its headers; a
/// build with no screen answers with words, never with a blank page. The files here are a stand-in set, so none of it depends on whether
/// the machine that built the tests had Node.js.
/// </summary>
public sealed class ScreenRoutesTests
{
    private const string Doc = """{"title":"Plan","tiles":[]}""";

    private const string Index = "<!doctype html><html lang=\"en\"><head><meta charset=\"UTF-8\"><title>Boards</title><script type=\"module\" src=\"./assets/app-1a2b3c.js\"></script></head><body><div id=\"root\"></div></body></html>";

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

    /// <summary>Ann may make boards and owns "plan", and every board she makes after it; Vic may only view "plan"; Zed has nothing to do with any.</summary>
    private sealed class Rules : IBoardAccess
    {
        private readonly Dictionary<(string User, string Board), BoardAction[]> _grants = new()
        {
            [("ann", "plan")] = [BoardAction.View, BoardAction.Edit, BoardAction.Publish, BoardAction.Delete],
            [("vic", "plan")] = [BoardAction.View],
        };

        public ValueTask<bool> AllowsAsync(ClaimsPrincipal user, BoardAction action, string? boardId, CancellationToken cancellationToken = default)
        {
            var name = user.Identity!.Name!;
            if (action == BoardAction.Create) return ValueTask.FromResult(name == "ann");
            return ValueTask.FromResult(boardId is not null && _grants.TryGetValue((name, boardId), out var actions) && actions.Contains(action));
        }

        public ValueTask CreatedAsync(ClaimsPrincipal user, string boardId, CancellationToken cancellationToken = default)
        {
            _grants[(user.Identity!.Name!, boardId)] = [BoardAction.View, BoardAction.Edit, BoardAction.Publish, BoardAction.Delete];
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoCatalog : IBoardSourceCatalog
    {
        public Task<BoardSourceCatalog> DescribeAsync(ClaimsPrincipal caller, bool includeUnreadable, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSourceCatalog([]));
    }

    private sealed class FakeScreen(IReadOnlyDictionary<string, ScreenAsset>? files) : IScreenAssets
    {
        public bool Available => files is not null && files.ContainsKey("index.html");

        public ScreenAsset? Find(string path) => files is not null && files.TryGetValue(path, out var found) ? found : null;
    }

    private static FakeScreen Screen(string index = Index) => new(new Dictionary<string, ScreenAsset>
    {
        ["index.html"] = new(Encoding.UTF8.GetBytes(index), "text/html; charset=utf-8"),
        ["assets/app-1a2b3c.js"] = new(Encoding.UTF8.GetBytes("console.log(1);"), "text/javascript; charset=utf-8"),
        ["THIRD-PARTY-NOTICES.txt"] = new(Encoding.UTF8.GetBytes("Third-party notices"), "text/plain; charset=utf-8"),
    });

    private sealed class Recording : IBoardAssistant
    {
        public List<BoardAssistantTurn> Turns { get; } = [];

        public Task<BoardAssistantAnswer> TakeTurnAsync(BoardAssistantTurn turn, CancellationToken cancellationToken = default)
        {
            Turns.Add(turn);
            return Task.FromResult(new BoardAssistantAnswer(BoardAssistantAnswer.Completed, "ok", [], [JsonDocument.Parse("""{"role":"assistant","content":"ok"}""").RootElement.Clone()]));
        }
    }

    private sealed class Host(WebApplication app, SqliteConnection connection, Recording assistant) : IAsyncDisposable
    {
        public HttpClient Client { get; } = app.GetTestServer().CreateClient();

        public Recording Assistant { get; } = assistant;

        public async Task<HttpResponseMessage> GetAsync(string path, string? user = "ann", Action<HttpRequestMessage>? adjust = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (user is not null) request.Headers.Add("X-Test-User", user);
            adjust?.Invoke(request);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static async Task<Host> StartAsync(IScreenAssets? screen = null, Action<IyuUBoardOptions>? configure = null, string? pathBase = null, bool assistant = false, bool makePlan = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var recording = new Recording();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UserHeaderHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<UBoardTestDb>(o => o.UseSqlite(connection));
        builder.Services.AddSingleton<IBoardAccess, Rules>();
        builder.Services.AddSingleton<IBoardSourceCatalog, NoCatalog>();
        if (screen is not null) builder.Services.AddSingleton(screen);
        if (assistant) builder.Services.AddSingleton<IBoardAssistant>(recording);
        builder.Services.AddIyuUBoard<UBoardTestDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>(configure);

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<UBoardTestDb>().Database.EnsureCreated();
        if (pathBase is not null) app.UsePathBase(pathBase);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();

        var host = new Host(app, connection, recording);
        if (makePlan)
        {
            var make = new HttpRequestMessage(HttpMethod.Post, $"{pathBase}/uboard/boards/plan?name=Plan") { Content = new StringContent(Doc, Encoding.UTF8, "application/json") };
            make.Headers.Add("X-Test-User", "ann");
            using var made = await host.Client.SendAsync(make);
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }

        return host;
    }

    private static Task<Host> StartWithScreenAsync(IScreenAssets? screen = null, string? pathBase = null, bool assistant = false, bool makePlan = true) =>
        StartAsync(screen ?? Screen(), o => o.Screen(), pathBase, assistant, makePlan);

    [Fact]
    public async Task A_board_page_is_the_built_page_with_the_hosts_settings_first_in_its_head_and_a_restrictive_policy()
    {
        await using var host = await StartWithScreenAsync();

        using var response = await host.GetAsync("/uboard/ui/boards/plan");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("<head><base href=\"/uboard/ui/\"><meta name=\"uboard-prefix\" content=\"/uboard\"><meta name=\"uboard-board\" content=\"plan\">", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("<base", StringComparison.Ordinal) < html.IndexOf("<script", StringComparison.Ordinal), "the base comes before any address the page loads");
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task The_policy_lets_a_page_load_only_from_its_own_host_runs_no_inline_script_and_is_framed_by_nobody()
    {
        await using var host = await StartWithScreenAsync();

        using var response = await host.GetAsync("/uboard/ui");
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var directives = policy.Split(';', StringSplitOptions.TrimEntries).ToDictionary(d => d.Split(' ')[0], d => d[(d.IndexOf(' ') + 1)..]);

        Assert.Equal(IyuUBoardScreenRoutes.ContentSecurityPolicy, policy);
        Assert.Equal("'none'", directives["default-src"]);
        Assert.Equal("'self'", directives["script-src"]); // no inline script, no eval
        Assert.Equal("'self'", directives["connect-src"]);
        Assert.Equal("'self'", directives["base-uri"]);
        Assert.Equal("'self'", directives["form-action"]);
        Assert.Equal("'none'", directives["frame-ancestors"]);
        Assert.Equal("'none'", directives["object-src"]);
        Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("http:", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("https:", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_does_not_say_which_boards_exist_a_board_the_person_may_not_view_and_one_that_is_not_there_are_the_same_404()
    {
        await using var host = await StartWithScreenAsync();

        using var hidden = await host.GetAsync("/uboard/ui/boards/plan", "zed");
        using var missing = await host.GetAsync("/uboard/ui/boards/nothing-here", "zed");
        using var viewer = await host.GetAsync("/uboard/ui/boards/plan", "vic");

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await hidden.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
    }

    [Fact]
    public async Task A_board_nothing_holds_yet_has_no_page_even_for_whoever_may_make_boards_and_a_board_made_is_the_makers_to_open()
    {
        await using var host = await StartWithScreenAsync();

        using var notYet = await host.GetAsync("/uboard/ui/boards/fresh-0001");
        using var made = new HttpRequestMessage(HttpMethod.Post, "/uboard/boards/fresh-0001?name=Fresh") { Content = new StringContent(Doc, Encoding.UTF8, "application/json") };
        made.Headers.Add("X-Test-User", "ann");
        using var created = await host.Client.SendAsync(made);
        using var now = await host.GetAsync("/uboard/ui/boards/fresh-0001");
        using var other = await host.GetAsync("/uboard/ui/boards/fresh-0001", "zed");

        Assert.Equal(HttpStatusCode.NotFound, notYet.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, now.StatusCode);
        Assert.Contains("name=\"uboard-board\" content=\"fresh-0001\"", await now.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task What_comes_from_the_address_is_written_into_the_page_encoded_so_it_cannot_close_the_tag_it_sits_in()
    {
        await using var host = await StartWithScreenAsync();
        var id = "a\"><script>alert(1)";
        using var made = new HttpRequestMessage(HttpMethod.Post, "/uboard/boards/" + Uri.EscapeDataString(id) + "?name=Odd") { Content = new StringContent(Doc, Encoding.UTF8, "application/json") };
        made.Headers.Add("X-Test-User", "ann");
        using var created = await host.Client.SendAsync(made);

        using var response = await host.GetAsync("/uboard/ui/boards/" + Uri.EscapeDataString(id));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("content=\"a&quot;&gt;&lt;script&gt;alert(1)\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_is_written_for_where_the_host_mounts_it()
    {
        await using var host = await StartWithScreenAsync(pathBase: "/app");

        using var response = await host.GetAsync("/app/uboard/ui/boards/plan");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<base href=\"/app/uboard/ui/\">", html, StringComparison.Ordinal);
        Assert.Contains("name=\"uboard-prefix\" content=\"/app/uboard\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_build_with_no_screen_answers_every_screen_route_with_503_and_says_why_after_the_application_has_been_asked()
    {
        await using var host = await StartWithScreenAsync(new FakeScreen(null));

        foreach (var path in new[] { "/uboard/ui", "/uboard/ui/boards/plan", "/uboard/ui/assets/app-1a2b3c.js", "/uboard/ui/third-party-notices.txt" })
        {
            using var response = await host.GetAsync(path);
            var words = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("not part of this build", words, StringComparison.Ordinal);
            Assert.Contains("Node.js", words, StringComparison.Ordinal);
        }

        // The person who may not open the board is told it is not there, not that the screen is missing.
        using var hidden = await host.GetAsync("/uboard/ui/boards/plan", "zed");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        // The board routes do not depend on the screen.
        using var board = await host.GetAsync("/uboard/boards/plan");
        Assert.Equal(HttpStatusCode.OK, board.StatusCode);
    }

    [Fact]
    public async Task A_page_that_has_no_head_to_write_into_is_an_error_that_says_so_not_a_page_without_its_settings()
    {
        await using var host = await StartWithScreenAsync(Screen("<html><body>no head</body></html>"));

        using var response = await host.GetAsync("/uboard/ui/boards/plan");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("no <head>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_files_a_page_loads_are_served_as_the_exact_files_they_are_with_their_type_and_kept_for_good_and_nothing_else_is()
    {
        await using var host = await StartWithScreenAsync();

        using var script = await host.GetAsync("/uboard/ui/assets/app-1a2b3c.js");
        using var notices = await host.GetAsync("/uboard/ui/third-party-notices.txt");
        using var unknown = await host.GetAsync("/uboard/ui/assets/nothing.js");
        using var index = await host.GetAsync("/uboard/ui/assets/index.html"); // the page is not one of the files under assets
        using var up = await host.GetAsync("/uboard/ui/assets/%2e%2e/index.html");

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", script.Content.Headers.ContentType!.ToString());
        Assert.True(script.Headers.CacheControl!.Private, "the files sit behind the sign-in, so no shared cache keeps them");
        Assert.Equal(TimeSpan.FromDays(365), script.Headers.CacheControl.MaxAge);
        Assert.Contains("immutable", script.Headers.CacheControl.ToString(), StringComparison.Ordinal);
        Assert.Equal("nosniff", script.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("console.log(1);", await script.Content.ReadAsStringAsync());
        Assert.Equal("text/plain; charset=utf-8", notices.Content.Headers.ContentType!.ToString());
        Assert.Equal("Third-party notices", await notices.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, index.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, up.StatusCode);
    }

    [Fact]
    public async Task A_request_from_another_site_or_from_no_one_is_refused_like_every_board_route()
    {
        await using var host = await StartWithScreenAsync();

        using var crossSite = await host.GetAsync("/uboard/ui/boards/plan", "ann", request => request.Headers.Add("Sec-Fetch-Site", "cross-site"));
        using var anonymous = await host.GetAsync("/uboard/ui", user: null);

        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task With_the_screen_not_switched_on_its_routes_are_not_there_though_the_boards_are()
    {
        await using var host = await StartAsync(Screen(), configure: null);

        using var page = await host.GetAsync("/uboard/ui/boards/plan");
        using var board = await host.GetAsync("/uboard/boards/plan");

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, board.StatusCode);
    }

    [Fact]
    public async Task A_screen_without_boards_does_not_start_and_a_data_root_that_is_not_a_path_is_refused_when_it_is_named()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIyuUBoard(o => o.Screen());
        await using var app = builder.Build();
        var refused = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
        Assert.Contains("Boards", refused.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => new IyuUBoardOptions().Screen("odata"));
        Assert.Throws<ArgumentException>(() => new IyuUBoardOptions().Screen("//elsewhere.test/odata"));
        Assert.Throws<ArgumentException>(() => new IyuUBoardOptions().Screen(" "));
    }

    [Fact]
    public async Task The_access_answer_says_what_the_host_has_switched_on_and_where_its_data_is()
    {
        await using var plain = await StartWithScreenAsync();
        await using var full = await StartAsync(Screen(), o => o.Screen("/odata/").Catalog(), assistant: true);

        var first = JsonNode.Parse(await (await plain.GetAsync("/uboard/access")).Content.ReadAsStringAsync())!;
        var second = JsonNode.Parse(await (await full.GetAsync("/uboard/access", "ann")).Content.ReadAsStringAsync())!;

        Assert.Equal((true, false, false, "/$data"), ((bool)first["create"]!, (bool)first["assistant"]!, (bool)first["catalog"]!, (string)first["dataRoot"]!));
        Assert.Equal((true, true, true, "/odata"), ((bool)second["create"]!, (bool)second["assistant"]!, (bool)second["catalog"]!, (string)second["dataRoot"]!));
    }

    // ---- the screen this build really carries (nothing here when the build had no Node.js; the stand-in tests above hold either way)

    [Fact]
    public void The_screen_a_build_carries_has_a_head_for_the_hosts_settings_runs_no_inline_script_and_brings_its_notices()
    {
        var assets = new EmbeddedScreenAssets();
        if (!assets.Available) return; // built without Node.js: the routes answer 503, as the tests above show

        var index = Encoding.UTF8.GetString(assets.Find("index.html")!.Content);
        var notices = Encoding.UTF8.GetString(assets.Find("THIRD-PARTY-NOTICES.txt")!.Content);

        Assert.NotNull(IyuUBoardScreenRoutes.Render(index, "/uboard", "plan")); // there is a <head> to write into
        foreach (Match script in Regex.Matches(index, "<script\\b[^>]*>"))
            Assert.Contains("src=", script.Value, StringComparison.Ordinal); // no inline script, which the policy would refuse
        Assert.DoesNotMatch(@"\son[a-z]+\s*=", index); // nor an inline event handler
        Assert.Contains("pretendard", notices, StringComparison.Ordinal);
        Assert.Contains("SIL OPEN FONT LICENSE", notices, StringComparison.Ordinal);
        foreach (Match asset in Regex.Matches(index, "(?:src|href)=\"\\./(assets/[^\"]+)\""))
            Assert.NotNull(assets.Find(asset.Groups[1].Value)); // every file the page names is in the package
    }
}
