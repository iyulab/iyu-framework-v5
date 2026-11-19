using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
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
/// The assistant's turn goes through the same questions as every other board route: a board the caller may not view is not there,
/// a caller who may not edit is handed an answer-only turn whose tools cannot change the board, a request that is not a plain turn
/// never reaches the assistant, and a host that registers no assistant has no turn route and says so. The assistant here is a
/// recording stand-in; what a real one does with the turn is tested with the adapter that provides it.
/// </summary>
public sealed class BoardAssistantTurnTests
{
    private const string Doc = """{"title":"Plan","tiles":[]}""";

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

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

    /// <summary>Ann owns "plan", Eve may edit it, Vic may only view it, Zed has nothing to do with it; Ann and Max may make boards.</summary>
    private sealed class Rules : IBoardAccess
    {
        private readonly Dictionary<(string User, string Board), BoardAction[]> _grants = new()
        {
            [("ann", "plan")] = [BoardAction.View, BoardAction.Edit, BoardAction.Publish, BoardAction.Delete],
            [("eve", "plan")] = [BoardAction.View, BoardAction.Edit],
            [("vic", "plan")] = [BoardAction.View],
            [("max", "fresh")] = [BoardAction.View],
            [("vic", "fresh")] = [BoardAction.View],
        };

        public ValueTask<bool> AllowsAsync(ClaimsPrincipal user, BoardAction action, string? boardId, CancellationToken cancellationToken = default)
        {
            var name = user.Identity!.Name!;
            if (action == BoardAction.Create) return ValueTask.FromResult(name is "ann" or "max");
            return ValueTask.FromResult(boardId is not null && _grants.TryGetValue((name, boardId), out var actions) && actions.Contains(action));
        }

        public ValueTask CreatedAsync(ClaimsPrincipal user, string boardId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>An assistant that records every turn it is handed and answers with <see cref="Answer"/> — or fails with <see cref="Failure"/>.</summary>
    private sealed class Recording : IBoardAssistant
    {
        public List<BoardAssistantTurn> Turns { get; } = [];

        public Exception? Failure { get; set; }

        public BoardAssistantAnswer Answer { get; set; } = new(
            BoardAssistantAnswer.Completed,
            "Here is what I found.",
            [],
            [JsonDocument.Parse("""{"role":"assistant","content":"Here is what I found.","opaque":[1,2]}""").RootElement.Clone()]);

        public Task<BoardAssistantAnswer> TakeTurnAsync(BoardAssistantTurn turn, CancellationToken cancellationToken = default)
        {
            Turns.Add(turn);
            return Failure is null ? Task.FromResult(Answer) : Task.FromException<BoardAssistantAnswer>(Failure);
        }
    }

    private sealed class Host(WebApplication app, SqliteConnection connection, Recording? assistant) : IAsyncDisposable
    {
        public HttpClient Client { get; } = app.GetTestServer().CreateClient();

        public Recording Assistant => assistant ?? throw new InvalidOperationException("this host has no assistant");

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string user, HttpContent? content = null, Action<HttpRequestMessage>? adjust = null)
        {
            var request = new HttpRequestMessage(method, "/uboard" + path) { Content = content };
            request.Headers.Add("X-Test-User", user);
            adjust?.Invoke(request);
            return await Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> TurnAsync(string board, string user, string json, Action<HttpRequestMessage>? adjust = null) =>
            SendAsync(HttpMethod.Post, $"/boards/{board}/turn", user, new StringContent(json, Encoding.UTF8, "application/json"), adjust);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>A body whose length is not known up front, as a chunked upload's is.</summary>
    private sealed class Unsized(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static async Task<Host> StartAsync(bool withAssistant = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UserHeaderHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<UBoardTestDb>(o => o.UseSqlite(connection));
        builder.Services.AddSingleton<IBoardAccess>(new Rules());
        builder.Services.AddIyuUBoard<UBoardTestDb, UBoardRow, UBoardRevisionRow, UBoardPublicationRow>();
        var assistant = withAssistant ? new Recording() : null;
        if (assistant is not null) builder.Services.AddSingleton<IBoardAssistant>(assistant);

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<UBoardTestDb>().Database.EnsureCreated();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();

        var host = new Host(app, connection, assistant);
        using var made = await host.SendAsync(HttpMethod.Post, "/boards/plan?name=Plan", "ann", new StringContent(Doc, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        return host;
    }

    private static string Turn(string message = "What sold best?", string tools = """[{"name":"read_catalog","schema":{"type":"object"},"edits":false}]""") =>
        $$"""{"system":"Rules.","history":[],"message":{{JsonSerializer.Serialize(message)}},"tools":{{tools}}}""";

    private static async Task<JsonNode> Read(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task A_host_with_no_assistant_has_no_turn_route_and_its_access_answer_says_so()
    {
        await using var host = await StartAsync(withAssistant: false);

        using var turn = await host.TurnAsync("plan", "ann", Turn());
        using var access = await host.SendAsync(HttpMethod.Get, "/access", "ann");

        Assert.Equal(HttpStatusCode.NotFound, turn.StatusCode);
        var answer = await Read(access);
        Assert.Equal(false, (bool?)answer["assistant"]);
        Assert.Equal(true, (bool?)answer["create"]);
    }

    [Fact]
    public async Task A_host_with_an_assistant_says_so_to_everyone_whether_or_not_they_may_make_boards()
    {
        await using var host = await StartAsync();

        foreach (var (user, mayCreate) in new[] { ("ann", true), ("vic", false) })
        {
            using var access = await host.SendAsync(HttpMethod.Get, "/access", user);
            var answer = await Read(access);
            Assert.Equal(true, (bool?)answer["assistant"]);
            Assert.Equal(mayCreate, (bool?)answer["create"]);
        }
    }

    [Fact]
    public async Task A_caller_who_may_not_view_the_board_is_told_it_is_not_there_and_the_assistant_never_hears_of_it()
    {
        await using var host = await StartAsync();

        using var hidden = await host.TurnAsync("plan", "zed", Turn());
        using var ghost = await host.TurnAsync("ghost", "ann", Turn());
        // Not even a request that is no turn at all tells them anything: the board is not there before the request is read.
        using var hiddenAndMalformed = await host.TurnAsync("plan", "zed", "not json");

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenAndMalformed.StatusCode);
        Assert.Empty(host.Assistant.Turns);
    }

    [Fact]
    public async Task A_caller_who_may_view_but_not_edit_gets_an_answer_only_turn_and_the_assistant_is_offered_no_tool_that_edits()
    {
        await using var host = await StartAsync();
        const string tools = """
            [{"name":"read_catalog","schema":{"type":"object"},"edits":false},
             {"name":"patch_board","schema":{"type":"object"},"edits":true},
             {"name":"says_nothing","schema":{"type":"object"}}]
            """;

        using var response = await host.TurnAsync("plan", "vic", Turn(tools: tools));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await Read(response);
        Assert.Equal(true, (bool?)answer["answerOnly"]);
        var turn = Assert.Single(host.Assistant.Turns);
        Assert.False(turn.CanEdit);
        // The one the screen vouched for as only reading is offered; the one that edits and the one that did not say are not.
        Assert.Equal(["read_catalog"], turn.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task A_caller_who_may_edit_gets_every_tool_and_the_board_as_it_stands_and_the_answer_is_not_marked_answer_only()
    {
        await using var host = await StartAsync();
        const string tools = """
            [{"name":"read_catalog","schema":{"type":"object"},"edits":false},
             {"name":"patch_board","description":"Changes the board.","schema":{"type":"object"},"edits":true},
             {"name":"says_nothing","schema":{"type":"object"}}]
            """;

        using var response = await host.TurnAsync("plan", "eve", Turn(tools: tools));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(false, (bool?)(await Read(response))["answerOnly"]);
        var turn = Assert.Single(host.Assistant.Turns);
        Assert.True(turn.CanEdit);
        Assert.Equal(["read_catalog", "patch_board", "says_nothing"], turn.Tools.Select(t => t.Name));
        Assert.Equal([false, true, true], turn.Tools.Select(t => t.Edits));
        Assert.Equal("Changes the board.", turn.Tools[1].Description);
        Assert.Equal("plan", turn.BoardId);
        Assert.Equal(1, turn.Board!.CurrentRevision);
        Assert.Equal("Plan", turn.Board.Name);
        Assert.Equal("eve", turn.User.Identity!.Name);
        Assert.Equal("What sold best?", turn.Message);
        Assert.Equal("Rules.", turn.System);
    }

    [Fact]
    public async Task A_board_that_is_not_saved_yet_may_be_started_by_whoever_may_make_boards_and_only_answered_about_for_anyone_else()
    {
        await using var host = await StartAsync();

        using var maker = await host.TurnAsync("fresh", "max", Turn());
        using var viewer = await host.TurnAsync("fresh", "vic", Turn());

        Assert.Equal(HttpStatusCode.OK, maker.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        Assert.Collection(
            host.Assistant.Turns,
            first =>
            {
                Assert.True(first.CanEdit);
                Assert.Null(first.Board);
            },
            second =>
            {
                Assert.False(second.CanEdit);
                Assert.Null(second.Board);
            });
    }

    [Fact]
    public async Task What_the_assistant_answers_goes_to_the_caller_and_the_history_is_the_assistants_own_to_hand_back()
    {
        await using var host = await StartAsync();
        host.Assistant.Answer = new BoardAssistantAnswer(
            BoardAssistantAnswer.AwaitingHostTools,
            "Let me look.",
            [new BoardToolCall("c1", "read_catalog", """{"entity":"Orders"}""")],
            [JsonDocument.Parse("""{"role":"assistant","calls":[{"id":"c1"}],"opaque":{"a":[1,2,3]}}""").RootElement.Clone()],
            JsonDocument.Parse("""{"tokens":15}""").RootElement.Clone());

        using var first = await host.TurnAsync("plan", "eve", Turn());
        var answer = await Read(first);

        Assert.Equal("AwaitingHostTools", (string?)answer["stop"]);
        Assert.Equal("Let me look.", (string?)answer["text"]);
        Assert.Equal("c1", (string?)answer["toolCalls"]![0]!["id"]);
        Assert.Equal("read_catalog", (string?)answer["toolCalls"]![0]!["name"]);
        Assert.Equal("""{"entity":"Orders"}""", (string?)answer["toolCalls"]![0]!["arguments"]);
        Assert.Equal(15, (int?)answer["usage"]!["tokens"]);

        // The next turn carries the history back as it came and the results of the tools; the layer passes both on without reading them.
        var history = answer["history"]!.ToJsonString();
        using var second = await host.TurnAsync(
            "plan", "eve",
            $$"""{"history":{{history}},"results":[{"callId":"c1","output":"3 entities"}],"final":"Stopped.","tools":[]}""");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var turn = host.Assistant.Turns[1];
        Assert.Null(turn.Message);
        Assert.Equal("Stopped.", turn.Final);
        var result = Assert.Single(turn.Results!);
        Assert.Equal(("c1", "3 entities"), (result.CallId, result.Output));
        Assert.Equal(history, JsonSerializer.Serialize(turn.History));
    }

    [Theory]
    [InlineData("""{"history":[],"tools":[]}""")] // nothing to say
    [InlineData("""{"message":"hi","results":[],"tools":[]}""")] // a message and results together
    [InlineData("""{"final":"x","message":"hi","tools":[]}""")] // an ending with no results to record
    [InlineData("""{"message":"hi"}""")] // no tools list
    [InlineData("""{"message":"hi","tools":[{"name":"two words","schema":{"type":"object"}}]}""")] // a name that is not a plain identifier
    [InlineData("""{"message":"hi","tools":[{"schema":{"type":"object"}}]}""")] // no name
    [InlineData("""{"message":"hi","tools":[{"name":"ok"}]}""")] // no schema
    [InlineData("""{"message":"hi","tools":[{"name":"ok","schema":[]}]}""")] // a schema that is not an object
    [InlineData("""{"results":[{"output":"x"}],"tools":[]}""")] // a result with no call
    [InlineData("""{"results":[{"callId":"c1"}],"tools":[]}""")] // a result with no output
    [InlineData("""[1,2]""")] // not a turn
    [InlineData("""null""")] // not a turn
    public async Task A_request_that_is_not_a_plain_turn_is_refused_and_never_reaches_the_assistant(string body)
    {
        await using var host = await StartAsync();

        using var response = await host.TurnAsync("plan", "eve", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Assistant.Turns);
    }

    [Fact]
    public async Task More_than_thirty_two_tools_is_refused_and_thirty_two_is_not()
    {
        await using var host = await StartAsync();
        static string Tools(int count) => "[" + string.Join(",", Enumerable.Range(0, count).Select(n => $$$"""{"name":"t{{{n}}}","schema":{"type":"object"}}""")) + "]";

        using var many = await host.TurnAsync("plan", "eve", Turn(tools: Tools(33)));
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Empty(host.Assistant.Turns);

        using var enough = await host.TurnAsync("plan", "eve", Turn(tools: Tools(32)));
        Assert.Equal(HttpStatusCode.OK, enough.StatusCode);
    }

    [Fact]
    public async Task A_request_that_is_not_JSON_is_refused_with_415()
    {
        await using var host = await StartAsync();

        using var response = await host.SendAsync(
            HttpMethod.Post, "/boards/plan/turn", "eve", new StringContent("message=hi", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(host.Assistant.Turns);
    }

    [Fact]
    public async Task A_request_with_no_body_at_all_is_refused_with_400()
    {
        await using var host = await StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post, "/boards/plan/turn", "eve");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Assistant.Turns);
    }

    [Fact]
    public async Task A_request_from_another_site_is_refused_with_403()
    {
        await using var host = await StartAsync();

        using var crossSite = await host.TurnAsync("plan", "eve", Turn(), r => r.Headers.Add("Sec-Fetch-Site", "cross-site"));
        using var otherOrigin = await host.TurnAsync("plan", "eve", Turn(), r => r.Headers.Add("Origin", "http://elsewhere.example"));

        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherOrigin.StatusCode);
        Assert.Empty(host.Assistant.Turns);
    }

    [Fact]
    public async Task A_caller_who_is_not_signed_in_is_refused_with_401()
    {
        await using var host = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/uboard/boards/plan/turn") { Content = new StringContent(Turn(), Encoding.UTF8, "application/json") };
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Assistant.Turns);
    }

    [Fact]
    public async Task A_request_over_one_mebibyte_is_refused_with_413_whether_or_not_its_size_is_announced()
    {
        await using var host = await StartAsync();
        var padding = new string('x', (1 << 20) + 1);
        var json = Turn(message: padding);

        using var announced = await host.TurnAsync("plan", "eve", json);
        using var unannounced = await host.SendAsync(
            HttpMethod.Post, "/boards/plan/turn", "eve", WithJson(new Unsized(Encoding.UTF8.GetBytes(json))));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, announced.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, unannounced.StatusCode);
        Assert.Empty(host.Assistant.Turns);
    }

    private static HttpContent WithJson(HttpContent content)
    {
        content.Headers.ContentType = new("application/json");
        return content;
    }

    [Fact]
    public async Task A_failure_of_the_assistant_tells_the_caller_its_kind_and_nothing_it_said()
    {
        await using var host = await StartAsync();

        host.Assistant.Failure = new BoardAssistantException(BoardAssistantException.RateLimit, "the service at https://models.internal.example said: key sk-secret is over its quota");
        using var busy = await host.TurnAsync("plan", "eve", Turn());
        host.Assistant.Failure = new BoardAssistantException("timeout", "took 90s on https://models.internal.example");
        using var slow = await host.TurnAsync("plan", "eve", Turn());
        host.Assistant.Failure = new InvalidOperationException("connection string Server=db.internal.example;Password=hunter2");
        using var broken = await host.TurnAsync("plan", "eve", Turn());
        host.Assistant.Failure = new BoardAssistantException(BoardAssistantException.BadRequest, "message 3 of the history is not a message");
        using var unreadable = await host.TurnAsync("plan", "eve", Turn());

        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.Equal("""{"error":"ratelimit"}""", await busy.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadGateway, slow.StatusCode);
        Assert.Equal("""{"error":"timeout"}""", await slow.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadGateway, broken.StatusCode);
        Assert.Equal("""{"error":"unknown"}""", await broken.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);
        Assert.Equal("""{"error":"badrequest"}""", await unreadable.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_caller_who_may_not_edit_still_cannot_change_the_board_by_running_an_edit_themselves()
    {
        await using var host = await StartAsync();

        // The screen would run its tools as the person: the save is what refuses, whatever the turn said.
        using var save = await host.SendAsync(HttpMethod.Put, "/boards/plan?expected=1", "vic", new StringContent(Doc, Encoding.UTF8, "application/json"));
        using var after = await host.SendAsync(HttpMethod.Get, "/boards/plan", "ann");

        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        Assert.Equal(1, (int?)(await Read(after))["revision"]);
    }

    [Fact]
    public void The_neutral_package_references_no_model_library_and_no_package_at_all()
    {
        var project = XDocument.Load(Path.Combine(PackageMetadataTests.SourceRoot(), "Iyu.UBoard", "Iyu.UBoard.csproj"));

        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("ProjectReference"));
        var references = project.Descendants("FrameworkReference").Select(r => (string?)r.Attribute("Include")).ToList();
        Assert.Equal(["Microsoft.AspNetCore.App"], references);
    }
}
