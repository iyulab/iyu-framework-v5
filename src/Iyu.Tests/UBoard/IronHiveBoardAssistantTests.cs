using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iyu.UBoard;
using Iyu.UBoard.EntityFrameworkCore;
using Iyu.UBoard.IronHive;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using static Iyu.Tests.UBoard.ScriptedModelService;

namespace Iyu.Tests.UBoard;

/// <summary>
/// The IronHive adapter fills the assistant port with IronHive.Agent over a model service the host names. The model service here is a
/// scripted stand-in on loopback: no key and no network, and every request the assistant sends it is kept to be read.
/// </summary>
public sealed class IronHiveBoardAssistantTests
{
    private const string HostKey = "sk-host-key";
    private const string ModelName = "the-host-model";

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{"entity":{"type":"string"}}}""").RootElement.Clone();

    private static readonly BoardToolDeclaration Read = new("read_catalog", "What the board can be built from.", Schema, Edits: false);

    private static readonly BoardToolDeclaration Patch = new("patch_board", "Changes the board.", Schema, Edits: true);

    private static ClaimsPrincipal Person(string name) => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "Test"));

    private static ServiceProvider Assistant(ScriptedModelService model, Action<IronHiveAssistantOptions>? adjust = null)
    {
        var services = new ServiceCollection();
        services.AddIyuUBoardIronHive(o =>
        {
            o.BaseUrl = model.BaseUrl;
            o.ApiKey = HostKey;
            o.Model = ModelName;
            o.MaxOutputTokens = 777;
            adjust?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    private static BoardAssistantTurn Message(string text, params BoardToolDeclaration[] tools) =>
        new(Person("ann"), "plan", null, true, "Rules of the board language.", [], text, null, null, tools);

    private static BoardAssistantTurn Results(IReadOnlyList<JsonElement> history, string? final, BoardToolResult[] results, params BoardToolDeclaration[] tools) =>
        new(Person("ann"), "plan", null, true, "Rules of the board language.", history, null, results, final, tools);

    [Fact]
    public async Task A_message_is_answered_with_the_tools_the_model_asked_for_and_the_host_named_the_model_and_held_the_key_and_the_limits()
    {
        await using var model = await StartAsync();
        model.Script(Reply.Ask(("c1", "read_catalog", """{"entity":"Orders"}""")));
        using var provider = Assistant(model);
        var assistant = provider.GetRequiredService<IBoardAssistant>();

        var answer = await assistant.TakeTurnAsync(Message("What sold best?", Read, Patch));

        Assert.Equal(BoardAssistantAnswer.AwaitingHostTools, answer.Stop);
        var call = Assert.Single(answer.ToolCalls);
        Assert.Equal(("c1", "read_catalog"), (call.Id, call.Name));
        Assert.Equal("Orders", (string?)JsonNode.Parse(call.Arguments)!["entity"]);
        Assert.NotEmpty(answer.History);

        var asked = Assert.Single(model.Requests);
        Assert.Equal(ModelName, (string?)asked.Body!["model"]);
        Assert.Equal($"Bearer {HostKey}", asked.Headers["authorization"]);
        Assert.Equal("Iyu.UBoard.IronHive", asked.Headers["user-agent"]);
        Assert.Equal("none", (string?)asked.Body["reasoning_effort"]);
        Assert.Equal(false, (bool?)asked.Body["chat_template_kwargs"]!["enable_thinking"]);
        Assert.Equal(777, (int?)(asked.Body["max_completion_tokens"] ?? asked.Body["max_tokens"]));
        Assert.Equal(["read_catalog", "patch_board"], asked.Body["tools"]!.AsArray().Select(t => (string?)t!["function"]!["name"]));
        var messages = asked.Body["messages"]!.AsArray();
        Assert.Equal("system", (string?)messages[0]!["role"]);
        Assert.Equal("Rules of the board language.", (string?)messages[0]!["content"]);
        Assert.Equal("What sold best?", (string?)messages[^1]!["content"]);
    }

    [Fact]
    public async Task The_results_of_the_tools_go_back_with_the_history_and_the_model_answers_in_words()
    {
        await using var model = await StartAsync();
        model.Script(
            Reply.Ask(("c1", "read_catalog", "{}")),
            Reply.Say("Orders sold best."));
        using var provider = Assistant(model);
        var assistant = provider.GetRequiredService<IBoardAssistant>();

        var first = await assistant.TakeTurnAsync(Message("What sold best?", Read));
        var second = await assistant.TakeTurnAsync(Results(first.History, null, [new BoardToolResult("c1", "Orders: Total, Status")], Read));

        Assert.Equal(BoardAssistantAnswer.Completed, second.Stop);
        Assert.Equal("Orders sold best.", second.Text);
        Assert.Empty(second.ToolCalls);
        var messages = model.Requests[1].Body!["messages"]!.AsArray();
        var tool = messages.Single(m => (string?)m!["role"] == "tool")!;
        Assert.Equal("c1", (string?)tool["tool_call_id"]);
        Assert.Equal("Orders: Total, Status", (string?)tool["content"]);
        // The conversation is the next turn's to carry: what the model said last is the end of it.
        Assert.Equal("assistant", (string?)JsonNode.Parse(second.History[^1].GetRawText())!["role"]);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(0, model.Remaining);
    }

    [Fact]
    public async Task A_turn_the_screen_ends_itself_records_the_results_and_its_words_and_the_model_is_not_asked()
    {
        await using var model = await StartAsync();
        model.Script(Reply.Ask(("c1", "patch_board", "{}")));
        using var provider = Assistant(model);
        var assistant = provider.GetRequiredService<IBoardAssistant>();
        var first = await assistant.TakeTurnAsync(Message("Make it red.", Patch));

        var closed = await assistant.TakeTurnAsync(Results(first.History, "Stopped.", [new BoardToolResult("c1", "Not run: stopped.")], Patch));

        Assert.Equal(BoardAssistantAnswer.Completed, closed.Stop);
        Assert.Equal("Stopped.", closed.Text);
        Assert.Single(model.Requests);
        var last = JsonNode.Parse(closed.History[^1].GetRawText())!;
        Assert.Equal("assistant", (string?)last["role"]);
        Assert.Contains("Stopped.", last.ToJsonString());
        Assert.Contains(closed.History, m => m.GetRawText().Contains("Not run: stopped."));
    }

    [Fact]
    public async Task A_history_that_is_not_a_conversation_is_a_bad_request_and_the_model_is_not_asked()
    {
        await using var model = await StartAsync();
        using var provider = Assistant(model);
        var assistant = provider.GetRequiredService<IBoardAssistant>();
        var history = new[] { JsonDocument.Parse("""{"role":42,"contents":"not a message"}""").RootElement.Clone() };

        var failure = await Assert.ThrowsAsync<BoardAssistantException>(() =>
            assistant.TakeTurnAsync(new BoardAssistantTurn(Person("ann"), "plan", null, true, null, history, "hi", null, null, [Read])));

        Assert.Equal(BoardAssistantException.BadRequest, failure.Kind);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData(429, BoardAssistantException.RateLimit)]
    [InlineData(401, "authentication")]
    [InlineData(500, "network")]
    public async Task A_model_service_that_refuses_is_a_failure_of_a_kind_and_what_it_said_is_not_in_the_exception_message(int status, string kind)
    {
        await using var model = await StartAsync();
        model.Script(Reply.Refuse(status), Reply.Refuse(status), Reply.Refuse(status), Reply.Refuse(status));
        using var provider = Assistant(model);
        var assistant = provider.GetRequiredService<IBoardAssistant>();

        var failure = await Assert.ThrowsAsync<BoardAssistantException>(() => assistant.TakeTurnAsync(Message("hi", Read)));

        Assert.Equal(kind, failure.Kind);
        Assert.DoesNotContain("sk-secret", failure.Message);
        Assert.DoesNotContain("models.internal.example", failure.Message);
    }

    [Fact]
    public async Task Pressing_stop_cancels_the_turn_and_is_not_dressed_up_as_a_failure_of_the_service()
    {
        await using var model = await StartAsync();
        model.Script(Reply.Slow(TimeSpan.FromSeconds(30), "Too late."));
        using var provider = Assistant(model);
        var assistant = provider.GetRequiredService<IBoardAssistant>();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assistant.TakeTurnAsync(Message("hi", Read), stop.Token));
    }

    [Fact]
    public async Task The_model_service_is_the_adapters_own_and_does_not_become_the_hosts_chat_client()
    {
        await using var model = await StartAsync();
        using var provider = Assistant(model);

        _ = provider.GetRequiredService<IBoardAssistant>();

        Assert.Null(provider.GetService<IChatClient>());
    }

    [Fact]
    public async Task A_host_that_names_no_address_or_model_does_not_start_and_the_message_names_what_is_missing_and_never_a_value()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        builder.Services.AddIyuUBoardIronHive(o => o.ApiKey = HostKey);
        using var app = builder.Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        Assert.Contains("BaseUrl", failure.Message);
        Assert.Contains("Model", failure.Message);
        Assert.DoesNotContain(HostKey, failure.Message);
    }

    [Fact]
    public async Task A_host_that_names_a_value_that_is_not_an_address_does_not_start_and_the_value_is_not_in_the_message()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        builder.Services.AddIyuUBoardIronHive(o =>
        {
            o.BaseUrl = "ftp://user:hunter2@models.internal.example";
            o.Model = ModelName;
        });
        using var app = builder.Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        Assert.Contains("BaseUrl", failure.Message);
        Assert.DoesNotContain("hunter2", failure.Message);
        Assert.DoesNotContain("models.internal.example", failure.Message);
    }

    [Fact]
    public void The_adapter_reads_nothing_from_the_environment_or_from_configuration_on_its_own()
    {
        var sources = Directory.GetFiles(Path.Combine(PackageMetadataTests.SourceRoot(), "Iyu.UBoard.IronHive"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("GetEnvironmentVariable", text);
            Assert.DoesNotContain("IConfiguration", text);
        }
    }

    // ---- the whole way: the route, the access questions, the adapter and a scripted model

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

    /// <summary>Ann may do everything to "plan"; Vic may only view it.</summary>
    private sealed class Rules : IBoardAccess
    {
        public ValueTask<bool> AllowsAsync(ClaimsPrincipal user, BoardAction action, string? boardId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(boardId == "plan" && (user.Identity!.Name == "ann" || (user.Identity.Name == "vic" && action == BoardAction.View)));

        public ValueTask CreatedAsync(ClaimsPrincipal user, string boardId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private static async Task<(WebApplication App, SqliteConnection Connection)> StartHostAsync(ScriptedModelService model)
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
        builder.Services.AddIyuUBoardIronHive(o =>
        {
            o.BaseUrl = model.BaseUrl;
            o.ApiKey = HostKey;
            o.Model = ModelName;
        });
        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<UBoardTestDb>().Database.EnsureCreated();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();

        using var made = await Send(app, HttpMethod.Post, "/uboard/boards/plan?name=Plan", "ann", """{"title":"Plan","tiles":[]}""");
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        return (app, connection);
    }

    private static async Task<HttpResponseMessage> Send(WebApplication app, HttpMethod method, string path, string user, string body)
    {
        using var client = app.GetTestServer().CreateClient();
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Test-User", user);
        return await client.SendAsync(request);
    }

    private const string ThreeTools = """
        [{"name":"read_catalog","description":"d","schema":{"type":"object"},"edits":false},
         {"name":"patch_board","description":"d","schema":{"type":"object"},"edits":true}]
        """;

    [Fact]
    public async Task Through_the_route_a_person_who_may_only_view_asks_a_model_that_is_offered_no_tool_that_edits_and_one_who_may_edit_is_offered_both()
    {
        await using var model = await StartAsync();
        model.Script(Reply.Say("Orders."), Reply.Say("Orders again."));
        var (app, connection) = await StartHostAsync(model);
        await using var disposeApp = app;
        await using var disposeConnection = connection;
        const string turn = """{"system":"Rules.","message":"What sold best?","tools":""" + ThreeTools + "}";

        using var viewer = await Send(app, HttpMethod.Post, "/uboard/boards/plan/turn", "vic", turn);
        using var editor = await Send(app, HttpMethod.Post, "/uboard/boards/plan/turn", "ann", turn);

        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        var answer = JsonNode.Parse(await viewer.Content.ReadAsStringAsync())!;
        Assert.Equal("Orders.", (string?)answer["text"]);
        Assert.Equal(true, (bool?)answer["answerOnly"]);
        Assert.Equal(["read_catalog"], model.Requests[0].Body!["tools"]!.AsArray().Select(t => (string?)t!["function"]!["name"]));

        Assert.Equal(HttpStatusCode.OK, editor.StatusCode);
        Assert.Equal(false, (bool?)JsonNode.Parse(await editor.Content.ReadAsStringAsync())!["answerOnly"]);
        Assert.Equal(["read_catalog", "patch_board"], model.Requests[1].Body!["tools"]!.AsArray().Select(t => (string?)t!["function"]!["name"]));
    }

    [Fact]
    public async Task Through_the_route_a_model_that_is_over_its_quota_is_a_429_with_its_kind_and_none_of_what_it_said()
    {
        await using var model = await StartAsync();
        model.Script(Reply.Refuse(429), Reply.Refuse(429), Reply.Refuse(429), Reply.Refuse(429));
        var (app, connection) = await StartHostAsync(model);
        await using var disposeApp = app;
        await using var disposeConnection = connection;

        using var response = await Send(app, HttpMethod.Post, "/uboard/boards/plan/turn", "ann", """{"message":"hi","tools":[]}""");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("""{"error":"ratelimit"}""", text);
    }

    [Fact]
    public async Task Through_the_route_a_history_that_is_not_a_conversation_is_a_400_and_the_model_is_not_asked()
    {
        await using var model = await StartAsync();
        var (app, connection) = await StartHostAsync(model);
        await using var disposeApp = app;
        await using var disposeConnection = connection;

        using var response = await Send(app, HttpMethod.Post, "/uboard/boards/plan/turn", "ann", """{"history":[{"role":42}],"message":"hi","tools":[]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Through_the_route_the_access_answer_says_the_assistant_is_on_once_the_adapter_is_registered()
    {
        await using var model = await StartAsync();
        var (app, connection) = await StartHostAsync(model);
        await using var disposeApp = app;
        await using var disposeConnection = connection;
        using var client = app.GetTestServer().CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/uboard/access");
        request.Headers.Add("X-Test-User", "vic");

        using var response = await client.SendAsync(request);
        var access = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(true, (bool?)access["assistant"]);
        Assert.Equal(false, (bool?)access["create"]);
    }
}
