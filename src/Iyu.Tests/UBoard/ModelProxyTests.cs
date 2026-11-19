using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Iyu.Tests.Server.OData;
using Iyu.UBoard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.UBoard;

/// <summary>
/// The model proxy sends an assistant's request to a service that speaks the OpenAI chat-completions wire, with the server's key.
/// The doubles here stand where that service stands; nothing leaves the process.
/// </summary>
public class ModelProxyTests
{
    private const string Key = "sk-test-key-do-not-leak";
    private const string Chat = "/uboard/model/chat/completions";

    private sealed class FakeModel : HttpMessageHandler
    {
        public int Calls;
        public Uri? Address;
        public string? Authorization;
        public JsonObject? Body;
        public Func<HttpResponseMessage> Reply = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"hello"}}]}""", Encoding.UTF8, "application/json"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Address = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            return Reply();
        }
    }

    private static async Task<(WebApplication App, FakeModel Model)> StartAsync(Action<IyuUBoardModelOptions>? tune = null, bool withModel = true)
    {
        var model = new FakeModel();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddIyuUBoard(o =>
        {
            if (withModel)
                o.Model(m =>
                {
                    m.BaseUrl = new Uri("https://model.example.test/v1/");
                    m.ApiKey = Key;
                    m.ModelName = "the-host-model";
                    m.MaxOutputTokens = 1000;
                    tune?.Invoke(m);
                });
            else
                o.Boards();
        });
        if (withModel)
            builder.Services.AddHttpClient(IyuUBoardEndpoints.ModelClientName).ConfigurePrimaryHttpMessageHandler(() => model);
        else
            builder.Services.AddSingleton<IBoardStore, EmptyStore>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();
        return (app, model);
    }

    private static HttpRequestMessage Post(string body, bool signedIn = true, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Chat) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (signedIn) request.Headers.Add("X-Test-Perm", "any");
        return request;
    }

    private const string Ask = """{"messages":[{"role":"user","content":"hi"}]}""";

    [Fact]
    public async Task The_request_goes_to_the_service_with_the_servers_key_and_the_reply_comes_back()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post(Ask));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hello", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("https://model.example.test/v1/chat/completions", model.Address!.ToString());
        Assert.Equal("Bearer " + Key, model.Authorization);
        Assert.Equal("hi", (string?)model.Body!["messages"]![0]!["content"]);
    }

    [Fact]
    public async Task The_page_cannot_choose_the_model_the_key_or_fields_it_was_not_given()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;
        var body = """
            {"messages":[{"role":"user","content":"hi"}],"model":"something-expensive","user":"someone","logit_bias":{"1":100},
             "stream_options":{"include_usage":true},"max_tokens":999999,"temperature":0.2,
             "tools":[{"type":"function","function":{"name":"patch_board"}}]}
            """;
        using var request = Post(body);
        request.Headers.Authorization = new("Bearer", "the-pages-own-token");

        using var response = await app.GetTestServer().CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = model.Body!;
        Assert.Equal("the-host-model", (string?)sent["model"]);
        Assert.Equal("Bearer " + Key, model.Authorization);
        Assert.Null(sent["user"]);
        Assert.Null(sent["logit_bias"]);
        Assert.Null(sent["stream_options"]);
        Assert.Equal(1000, (int?)sent["max_tokens"]);
        Assert.Equal(0.2, (double?)sent["temperature"]);
        Assert.Equal("patch_board", (string?)sent["tools"]![0]!["function"]!["name"]);
    }

    [Theory]
    [InlineData("\"max_tokens\":\"999999\"")]
    [InlineData("\"max_tokens\":1e9")]
    [InlineData("\"max_tokens\":99999999999999999999")]
    [InlineData("\"max_completion_tokens\":1e9")]
    [InlineData("\"max_tokens\":1e400")]
    [InlineData("\"max_tokens\":null")]
    public async Task A_limit_the_page_sends_in_any_shape_never_reaches_the_service_above_the_hosts_limit(string field)
    {
        var (app, model) = await StartAsync();
        await using var _ = app;

        using var response = await app.GetTestServer().CreateClient()
            .SendAsync(Post("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]," + field + "}"));

        // Either the value was lowered to the limit or the request was refused; what the page wrote never goes through.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            foreach (var name in new[] { "max_tokens", "max_completion_tokens" })
                if (model.Body![name] is { } sent) Assert.InRange((long)sent, 1, 1000);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, model.Calls);
        }
    }

    [Theory]
    [InlineData("\"max_tokens\":0")]
    [InlineData("\"max_tokens\":-5")]
    [InlineData("\"max_tokens\":12.5")]
    [InlineData("\"max_completion_tokens\":\"lots\"")]
    public async Task A_limit_that_is_not_a_whole_number_above_zero_is_a_400_and_nothing_is_sent(string field)
    {
        var (app, model) = await StartAsync();
        await using var _ = app;

        using var response = await app.GetTestServer().CreateClient()
            .SendAsync(Post("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]," + field + "}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_request_that_asks_for_no_limit_is_given_the_hosts_limit_in_the_configured_field()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;
        var (newer, newerModel) = await StartAsync(m => m.OutputLimitField = "max_completion_tokens");
        await using var __ = newer;

        using var first = await app.GetTestServer().CreateClient().SendAsync(Post(Ask));
        using var second = await newer.GetTestServer().CreateClient().SendAsync(Post(Ask));

        Assert.Equal(1000, (int?)model.Body!["max_tokens"]);
        Assert.Null(model.Body["max_completion_tokens"]);
        Assert.Equal(1000, (int?)newerModel.Body!["max_completion_tokens"]);
        Assert.Null(newerModel.Body["max_tokens"]);
    }

    [Fact]
    public async Task A_host_that_names_a_limit_field_the_wire_does_not_have_does_not_start()
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(m => m.OutputLimitField = "max_everything"));

        Assert.Contains("OutputLimitField", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fields_the_host_pins_go_to_the_service_with_every_request_and_the_page_cannot_change_them()
    {
        var (app, model) = await StartAsync(m => m.ExtraBody = JsonNode.Parse("""{"chat_template_kwargs":{"enable_thinking":false},"temperature":0.1}""")!.AsObject());
        await using var _ = app;
        // The page tries to turn thinking back on and to set the temperature; only the host's values reach the service.
        var body = """{"messages":[{"role":"user","content":"hi"}],"chat_template_kwargs":{"enable_thinking":true},"temperature":0.9}""";

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((bool?)model.Body!["chat_template_kwargs"]!["enable_thinking"]);
        Assert.Equal(0.1, (double?)model.Body["temperature"]);
        Assert.Equal("the-host-model", (string?)model.Body["model"]);
    }

    [Fact]
    public async Task Without_pinned_fields_a_field_outside_the_allow_list_is_still_dropped()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;

        using var response = await app.GetTestServer().CreateClient()
            .SendAsync(Post("""{"messages":[{"role":"user","content":"hi"}],"chat_template_kwargs":{"enable_thinking":true}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(model.Body!["chat_template_kwargs"]);
    }

    [Fact]
    public async Task A_pinned_reply_length_is_still_held_to_the_hosts_limit()
    {
        var (app, model) = await StartAsync(m => m.ExtraBody = JsonNode.Parse("""{"max_tokens":999999}""")!.AsObject());
        await using var _ = app;

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post(Ask));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1000, (int?)model.Body!["max_tokens"]);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("messages")]
    [InlineData("stream")]
    [InlineData("tools")]
    [InlineData("tool_choice")]
    public async Task A_host_that_pins_a_field_the_page_alone_may_write_does_not_start(string field)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(m => m.ExtraBody = JsonNode.Parse("{\"" + field + "\":\"x\"}")!.AsObject()));

        Assert.Contains("ExtraBody", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains(field, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_is_passed_on_as_a_stream()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;
        const string Sse = "data: {\"choices\":[{\"delta\":{\"content\":\"he\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"llo\"}}]}\n\ndata: [DONE]\n\n";
        model.Reply = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream") };

        using var response = await app.GetTestServer().CreateClient()
            .SendAsync(Post("""{"messages":[{"role":"user","content":"hi"}],"stream":true}"""));

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Sse, await response.Content.ReadAsStringAsync());
        Assert.True((bool?)model.Body!["stream"]);
    }

    [Fact]
    public async Task A_service_that_refuses_is_a_502_without_its_words_and_never_the_key()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;
        model.Reply = () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent($"{{\"error\":\"Incorrect API key provided: {Key}\"}}", Encoding.UTF8, "application/json"),
        };

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post(Ask));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Incorrect API key", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_that_is_rate_limiting_is_a_429_with_its_wait()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;
        model.Reply = () =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
            return limited;
        };

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post(Ask));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("7", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task A_service_that_cannot_be_reached_is_a_502()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;
        model.Reply = () => throw new HttpRequestException("connection refused at model.example.test");

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post(Ask));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("model.example.test", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_before_the_service_is_called()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post(Ask, signedIn: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Origin", "https://another.example.test")]
    public async Task A_request_another_site_sent_with_the_callers_cookie_is_refused(string header, string value)
    {
        var (app, model) = await StartAsync();
        await using var _ = app;
        using var request = Post(Ask);
        request.Headers.Add(header, value);

        using var response = await app.GetTestServer().CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_refused_before_the_service_is_called()
    {
        var (app, model) = await StartAsync();
        await using var _ = app;

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post("messages=hi", contentType: "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"prompt":"no messages"}""")]
    public async Task A_body_without_a_message_list_is_a_400_and_nothing_is_sent(string body)
    {
        var (app, model) = await StartAsync();
        await using var _ = app;

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_a_413_whether_or_not_it_says_how_long_it_is()
    {
        var (app, model) = await StartAsync(m => m.MaxRequestBytes = 200);
        await using var _ = app;
        var big = """{"messages":[{"role":"user","content":"%"}]}""".Replace("%", new string('x', 500));

        using var declared = await app.GetTestServer().CreateClient().SendAsync(Post(big));
        using var chunked = Post(big);
        chunked.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big)));
        chunked.Content.Headers.ContentType = new("application/json");
        chunked.Headers.TransferEncodingChunked = true;
        using var undeclared = await app.GetTestServer().CreateClient().SendAsync(chunked);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, undeclared.StatusCode);
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("", "key")]
    public async Task A_host_that_turns_the_model_on_without_a_key_does_not_start_and_the_message_names_no_value(string? key, string missing)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(m => m.ApiKey = key));

        var text = failure.ToString();
        Assert.Contains("ApiKey", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        _ = missing;
    }

    [Fact]
    public async Task A_host_that_turns_the_model_on_without_an_address_or_model_does_not_start()
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(m => { m.BaseUrl = null; m.ModelName = " "; }));

        Assert.Contains("BaseUrl", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("ModelName", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_that_did_not_turn_the_model_on_has_no_model_route()
    {
        var (app, _) = await StartAsync(withModel: false);
        await using var __ = app;

        using var response = await app.GetTestServer().CreateClient().SendAsync(Post(Ask));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class EmptyStore : IBoardStore
    {
        // The route group does not touch the store; it only has to exist for the start-up check.
        public Task<BoardChange> CreateAsync(string id, string name, string document, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BoardRecord?> FindAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BoardChange> SaveAsync(string id, string document, int expectedRevision, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BoardRevisionRecord?> FindRevisionAsync(string id, int revision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BoardRevisionRecord>> ListRevisionsAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BoardPublicationRecord?> PublishAsync(string id, int? revision, string publishedBy, DateTimeOffset at, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BoardPublicationRecord>> ListPublicationsAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BoardSummary>> ListAsync(int skip, int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BoardChange> RenameAsync(string id, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
