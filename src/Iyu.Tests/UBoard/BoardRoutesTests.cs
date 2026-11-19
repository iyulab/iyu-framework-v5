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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.UBoard;

/// <summary>
/// <c>MapIyuUBoard</c> puts the board layer behind routes under <c>{prefix}/boards</c>: the status codes and the bodies a page relies
/// on, a signed-in caller, a same-origin request, and a refusal of a body that is not JSON. The store is a real SQLite database.
/// </summary>
public sealed class BoardRoutesTests
{
    private const string Doc = """{"title":"Sales","tiles":[]}""";

    /// <summary>A caller who says who they are in <c>X-Test-User</c> (and, if they like, a subject in <c>X-Test-Sub</c>); no header, no caller.</summary>
    private sealed class UserHeaderHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>();
            if (Request.Headers.TryGetValue("X-Test-User", out var name)) claims.Add(new Claim(ClaimTypes.Name, name.ToString()));
            if (Request.Headers.TryGetValue("X-Test-Sub", out var sub)) claims.Add(new Claim("sub", sub.ToString()));
            if (Request.Headers.TryGetValue("X-Test-Perm", out var perm)) claims.Add(new Claim("perm", perm.ToString()));
            if (claims.Count == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(claims, "Test", ClaimTypes.Name, "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly SqliteConnection _connection;

        public HttpClient Client { get; }

        public Host(WebApplication app, SqliteConnection connection)
        {
            _app = app;
            _connection = connection;
            Client = app.GetTestServer().CreateClient();
        }

        public HttpRequestMessage Request(HttpMethod method, string path, string? body = Doc, string user = "ada", string contentType = "application/json")
        {
            var request = new HttpRequestMessage(method, "/uboard/boards" + path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
            if (user.Length > 0) request.Headers.Add("X-Test-User", user);
            return request;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null, string user = "ada") =>
            await Client.SendAsync(Request(method, path, body, user));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static async Task<Host> StartAsync(string? policy = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UserHeaderHandler>("Test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy("boards.use", p => p.RequireClaim("perm", "boards")));
        builder.Services.AddDbContext<UBoardTestDb>(o => o.UseSqlite(connection));
        builder.Services.AddSingleton<IBoardAccess, EveryoneSignedInBoardAccess>();
        builder.Services.AddIyuUBoard<UBoardTestDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>(o => o.Policy = policy);

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<UBoardTestDb>().Database.EnsureCreated();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();
        return new Host(app, connection);
    }

    private static async Task<JsonNode> Read(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task A_board_is_made_with_201_and_read_back_with_its_document_as_json()
    {
        await using var host = await StartAsync();

        using var made = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales%20board"));
        var envelope = await Read(made);

        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal("sales", (string?)envelope["id"]);
        Assert.Equal("Sales board", (string?)envelope["name"]);
        Assert.Equal(1, (int?)envelope["revision"]);
        Assert.Null(envelope["publishedRevision"]);
        Assert.Equal("Sales", (string?)envelope["document"]!["title"]);

        using var read = await host.SendAsync(HttpMethod.Get, "/sales");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(1, (int?)(await Read(read))["revision"]);
    }

    [Fact]
    public async Task Making_a_board_that_exists_is_409_and_answers_with_the_board_as_it_stands()
    {
        await using var host = await StartAsync();
        using var _ = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=First"));

        using var again = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Second", body: """{"other":1}"""));
        var envelope = await Read(again);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("First", (string?)envelope["name"]);
        Assert.Equal("Sales", (string?)envelope["document"]!["title"]);
    }

    [Fact]
    public async Task A_board_that_is_not_there_is_404_to_read_save_rename_delete_publish_withdraw_and_to_its_revisions()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(host.Request(HttpMethod.Put, "/nope?expected=1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Put, "/nope/name?name=x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Delete, "/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/nope/publish?revision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/nope/withdraw")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/nope/revisions/1")).StatusCode);
    }

    [Fact]
    public async Task A_save_that_names_the_current_revision_adds_one_and_a_stale_save_is_409_with_the_board_as_it_stands()
    {
        await using var host = await StartAsync();
        using var _ = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales"));

        using var saved = await host.Client.SendAsync(host.Request(HttpMethod.Put, "/sales?expected=1", body: """{"title":"Second"}"""));
        using var stale = await host.Client.SendAsync(host.Request(HttpMethod.Put, "/sales?expected=1", body: """{"title":"Lost"}"""));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2, (int?)(await Read(saved))["revision"]);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var now = await Read(stale);
        Assert.Equal(2, (int?)now["revision"]);
        Assert.Equal("Second", (string?)now["document"]!["title"]);
    }

    [Fact]
    public async Task Revisions_are_listed_and_each_is_read_with_its_document_and_who_saved_it()
    {
        await using var host = await StartAsync();
        using var _ = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales", user: "ada"));
        using var __ = await host.Client.SendAsync(host.Request(HttpMethod.Put, "/sales?expected=1", body: """{"title":"Second"}""", user: "grace"));

        var list = (JsonArray)await Read(await host.SendAsync(HttpMethod.Get, "/sales/revisions"));
        using var one = await host.SendAsync(HttpMethod.Get, "/sales/revisions/1");
        var first = await Read(one);

        Assert.Equal(2, list.Count);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal("Sales", (string?)first["document"]!["title"]);
        Assert.Equal("ada", (string?)first["createdBy"]);
        Assert.Equal("grace", (string?)list.Single(r => (int?)r!["revision"] == 2)!["createdBy"]);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/sales/revisions/9")).StatusCode);
    }

    [Fact]
    public async Task Publishing_moves_the_pointer_reads_show_the_published_revision_and_withdrawing_leaves_204()
    {
        await using var host = await StartAsync();
        using var _ = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales"));
        using var __ = await host.Client.SendAsync(host.Request(HttpMethod.Put, "/sales?expected=1", body: """{"title":"Second"}"""));

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Get, "/sales/published")).StatusCode);

        using var published = await host.SendAsync(HttpMethod.Post, "/sales/publish?revision=1");
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal("ada", (string?)(await Read(published))["publishedBy"]);
        var reader = await Read(await host.SendAsync(HttpMethod.Get, "/sales/published"));
        Assert.Equal(1, (int?)reader["revision"]);
        Assert.Equal("Sales", (string?)reader["document"]!["title"]);
        Assert.Equal(1, (int?)(await Read(await host.SendAsync(HttpMethod.Get, "/sales")))["publishedRevision"]);

        using var withdrawn = await host.SendAsync(HttpMethod.Post, "/sales/withdraw");
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Get, "/sales/published")).StatusCode);
        Assert.Equal(2, ((JsonArray)await Read(await host.SendAsync(HttpMethod.Get, "/sales/publications"))).Count);
    }

    [Fact]
    public async Task Publishing_a_revision_the_board_does_not_have_is_404()
    {
        await using var host = await StartAsync();
        using var _ = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales"));

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/sales/publish?revision=7")).StatusCode);
    }

    [Fact]
    public async Task The_fallback_lists_the_publications_before_the_one_in_force_newest_first_with_their_documents()
    {
        await using var host = await StartAsync();
        using var _ = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales"));
        using var __ = await host.Client.SendAsync(host.Request(HttpMethod.Put, "/sales?expected=1", body: """{"title":"Two"}"""));
        using var ___ = await host.Client.SendAsync(host.Request(HttpMethod.Put, "/sales?expected=2", body: """{"title":"Three"}"""));
        foreach (var revision in new[] { 1, 2, 3 })
            Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, $"/sales/publish?revision={revision}")).StatusCode);

        var earlier = (JsonArray)await Read(await host.SendAsync(HttpMethod.Get, "/sales/fallback"));

        Assert.Equal([2, 1], earlier.Select(e => (int)e!["revision"]!));
        Assert.Equal("Two", (string?)earlier[0]!["document"]!["title"]);
    }

    [Fact]
    public async Task The_list_pages_by_skip_and_take_without_documents_and_a_page_out_of_range_is_400()
    {
        await using var host = await StartAsync();
        foreach (var id in new[] { "a", "b", "c" })
            using (await host.Client.SendAsync(host.Request(HttpMethod.Post, $"/{id}?name=Board%20{id}"))) { }

        var all = (JsonArray)await Read(await host.SendAsync(HttpMethod.Get, ""));
        var page = (JsonArray)await Read(await host.SendAsync(HttpMethod.Get, "?skip=1&take=1"));

        Assert.Equal(["a", "b", "c"], all.Select(b => (string)b!["id"]!));
        Assert.Equal("b", (string?)page.Single()!["id"]);
        Assert.Null(page[0]!["document"]);
        Assert.Equal(1, (int?)page[0]!["revision"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Get, "?take=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Get, "?skip=-1")).StatusCode);
    }

    [Fact]
    public async Task Renaming_changes_the_name_and_keeps_the_document_and_a_blank_name_is_400()
    {
        await using var host = await StartAsync();
        using var _ = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Old"));

        using var renamed = await host.SendAsync(HttpMethod.Put, "/sales/name?name=New%20name");
        var envelope = await Read(renamed);

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("New name", (string?)envelope["name"]);
        Assert.Equal(1, (int?)envelope["revision"]);
        Assert.Equal("Sales", (string?)envelope["document"]!["title"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Put, "/sales/name?name=%20")).StatusCode);
    }

    [Fact]
    public async Task Deleting_removes_the_board_with_204_and_a_second_delete_is_404()
    {
        await using var host = await StartAsync();
        using var _ = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales"));

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Delete, "/sales")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/sales")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Delete, "/sales")).StatusCode);
    }

    [Fact]
    public async Task A_value_the_service_refuses_is_400_with_a_problem_not_a_server_error()
    {
        await using var host = await StartAsync();

        using var padded = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/%20sales?name=Sales"));
        using var notJson = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales", body: "this is not json"));
        using var noName = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=%20"));
        using var empty = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales", body: ""));

        Assert.Equal(HttpStatusCode.BadRequest, padded.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("problem", padded.Content.Headers.ContentType!.MediaType, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/sales")).StatusCode);
    }

    [Fact]
    public async Task A_caller_who_is_not_signed_in_gets_401_on_every_kind_of_route()
    {
        await using var host = await StartAsync();

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, ""), (HttpMethod.Get, "/sales"), (HttpMethod.Post, "/sales?name=x"),
                     (HttpMethod.Put, "/sales?expected=1"), (HttpMethod.Delete, "/sales"), (HttpMethod.Get, "/sales/published"),
                 })
        {
            using var response = await host.Client.SendAsync(host.Request(method, path, body: null, user: ""));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_policy_the_host_names_is_what_the_board_routes_require()
    {
        await using var host = await StartAsync("boards.use");

        using var without = await host.SendAsync(HttpMethod.Get, "");
        using var with = await host.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/uboard/boards") { Headers = { { "X-Test-User", "ada" }, { "X-Test-Perm", "boards" } } });

        Assert.Equal(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    [Fact]
    public async Task A_request_from_another_site_is_403_whether_it_reads_or_writes()
    {
        await using var host = await StartAsync();

        using var crossSite = host.Request(HttpMethod.Get, "", body: null);
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var foreignOrigin = host.Request(HttpMethod.Post, "/sales?name=Sales");
        foreignOrigin.Headers.Add("Origin", "https://evil.example.test");
        using var sameOrigin = host.Request(HttpMethod.Get, "", body: null);
        sameOrigin.Headers.Add("Sec-Fetch-Site", "same-origin");

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(crossSite)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(foreignOrigin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(sameOrigin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/sales")).StatusCode);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_415_while_a_read_and_a_request_without_a_body_need_no_content_type()
    {
        await using var host = await StartAsync();

        using var form = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales", body: "title=x", contentType: "application/x-www-form-urlencoded"));
        using var made = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/sales?name=Sales"));
        using var publish = await host.SendAsync(HttpMethod.Post, "/sales/publish?revision=1");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/sales")).StatusCode);
    }

    [Fact]
    public async Task The_history_names_the_caller_by_name_then_by_subject_and_a_document_over_the_limit_is_413()
    {
        await using var host = await StartAsync();

        using var named = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/a?name=A", user: "ada"));
        var bySubject = host.Request(HttpMethod.Post, "/b?name=B", user: "");
        bySubject.Headers.Add("X-Test-Sub", "subject-7");
        using var subject = await host.Client.SendAsync(bySubject);
        var huge = new string('x', 4 * 1024 * 1024 + 1);
        using var large = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/c?name=C", body: $"{{\"x\":\"{huge}\"}}"));

        Assert.Equal("ada", (string?)(await Read(await host.SendAsync(HttpMethod.Get, "/a/revisions/1")))["createdBy"]);
        Assert.Equal("subject-7", (string?)(await Read(await host.SendAsync(HttpMethod.Get, "/b/revisions/1")))["createdBy"]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
    }
}
