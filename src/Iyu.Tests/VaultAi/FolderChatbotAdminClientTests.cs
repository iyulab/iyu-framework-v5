using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iyu.VaultAi;
using Iyu.VaultAi.FolderChatbots;
using Xunit;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// The management client, against a fake server: what it asks of vault-ai (the shape of each request) and how it reads what comes back.
/// The tests that matter most are the ones about what must not be sent short — an agent update replaces the agent whole.
/// </summary>
public class FolderChatbotAdminClientTests
{
    private sealed record Seen(HttpMethod Method, string PathAndQuery, string? Body, IReadOnlyList<(string Name, string Value)> FormFields,
        string? UploadedFileName);

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            var fields = new List<(string, string)>();
            string? uploaded = null;
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form)
                {
                    var disposition = part.Headers.ContentDisposition;
                    var content = await part.ReadAsStringAsync(cancellationToken);
                    if (disposition?.FileName is { } name) uploaded = Uri.UnescapeDataString(name.Trim('"'));
                    else fields.Add((disposition?.Name?.Trim('"') ?? "", content));
                }
            }
            else if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            Requests.Add(new Seen(request.Method, request.RequestUri!.PathAndQuery, body, fields, uploaded));
            var response = Respond(request);
            response.RequestMessage ??= request;   // a real HttpClient sets it; the client reads the method from it
            return response;
        }
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static (FolderChatbotAdminClient Client, FakeServer Server) Make(TimeSpan? timeout = null)
    {
        var server = new FakeServer();
        var http = new HttpClient(server) { BaseAddress = new Uri("https://vault.example/") };
        return (new FolderChatbotAdminClient(http, timeout ?? TimeSpan.FromSeconds(30)), server);
    }

    private static readonly Guid Id1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Id2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    // ---- lists

    [Fact]
    public async Task A_prefixed_list_asks_the_server_for_the_prefix_and_keeps_only_exact_case_matches()
    {
        var (client, server) = Make();
        server.Respond = _ => Json(new
        {
            count = 3,
            data = new object[]
            {
                new { id = Id1, name = "acmeall", description = "d" },
                new { id = Id2, name = "ACMEall", description = "wrong case — the server's ^ ignores case" },
                new { id = Guid.NewGuid(), name = "acme-other", description = "" },
            },
        });

        var storages = await client.ListAgentsAsync("acme");

        Assert.Equal(["acmeall", "acme-other"], storages.Select(s => s.Name));
        var request = Assert.Single(server.Requests);
        Assert.StartsWith("/api/agents?filter=name%5Eacme&pageSize=100&page=1&orderBy=createdAt", request.PathAndQuery);
    }

    [Fact]
    public async Task A_filter_value_is_escaped_for_the_servers_filter_syntax()
    {
        var (client, server) = Make();
        server.Respond = _ => Json(new { count = 0, data = Array.Empty<object>() });

        await client.ListChannelsAsync(@"a,b|c(d)\e");

        var query = Uri.UnescapeDataString(Assert.Single(server.Requests).PathAndQuery);
        Assert.Contains(@"filter=name^a\,b\|c\(d\)\\e", query);
    }

    [Fact]
    public async Task A_list_is_read_page_by_page_until_the_count_is_reached_not_until_a_short_page()
    {
        var (client, server) = Make();
        var pages = new Queue<object>([
            new { count = 3, data = new object[] { new { id = Id1, fileName = "a/1.pdf", contentSize = 1, status = "indexed", tags = new[] { "bot:a" } } } },
            new { count = 3, data = new object[] { new { id = Id2, fileName = "a/2.pdf", contentSize = 2, status = "pending", tags = new[] { "bot:a" } },
                                                   new { id = Guid.NewGuid(), fileName = "a/3.pdf", contentSize = 3, status = "indexed", tags = (string[]?)null } } },
        ]);
        server.Respond = _ => Json(pages.Dequeue());

        var files = await client.ListFilesAsync(Id1);

        Assert.Equal(["a/1.pdf", "a/2.pdf", "a/3.pdf"], files.Select(f => f.FileName));
        Assert.Equal(["bot:a"], files[0].Tags);
        Assert.Empty(files[2].Tags);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("page=2", server.Requests[1].PathAndQuery);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_prefixed_list_refuses_a_blank_prefix_rather_than_listing_the_whole_workspace(string prefix)
    {
        var (client, server) = Make();

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListStoragesAsync(prefix));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAgentsAsync(prefix));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListChannelsAsync(prefix));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListConnectorsAsync(prefix));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Models_are_read_with_their_kind_lowercased_and_the_context_length_from_a_number_or_a_numeric_string()
    {
        var (client, server) = Make();
        server.Respond = _ => Json(new
        {
            count = 4,
            data = new object[]
            {
                new { id = Id1, modelId = "m1", displayName = "One", type = "Language", metadata = new { contextLength = 128000 } },
                new { id = Id2, modelId = "m2", displayName = "", type = "language", metadata = new { ContextLength = "65536" } },
                new { id = Guid.NewGuid(), modelId = "m3", displayName = "Three", type = "embedding", metadata = new { other = 1 } },
                new { id = Guid.NewGuid(), modelId = "m4", displayName = "Four", type = "language", metadata = (object?)null },
            },
        });

        var models = await client.ListModelsAsync();

        Assert.Equal("language", models[0].Type);
        Assert.Equal(128000, models[0].ContextLength);
        Assert.Equal(65536, models[1].ContextLength);
        Assert.Null(models[2].ContextLength);
        Assert.Null(models[3].ContextLength);
    }

    // ---- storage

    [Fact]
    public async Task A_storage_update_sends_the_whole_body_with_null_models_so_a_cleared_model_is_cleared()
    {
        var (client, server) = Make();

        await client.UpdateStorageAsync(Id1, "acmeall", "chatbot-meta:{}", embedding: Id2, language: null, rerank: null);

        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/api/storages/{Id1}", request.PathAndQuery);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("acmeall", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("chatbot-meta:{}", body.RootElement.GetProperty("description").GetString());
        Assert.Equal(Id2, body.RootElement.GetProperty("embeddingModelId").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("languageModelId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("rerankModelId").ValueKind);
    }

    [Fact]
    public async Task A_storage_summary_asks_for_the_tags_given()
    {
        var (client, server) = Make();
        server.Respond = _ => Json(new { totalCount = 5, byStatus = new Dictionary<string, int> { ["indexed"] = 4, ["pending"] = 1 } });

        var summary = await client.GetStorageSummaryAsync(Id1, ["bot:a", "bot:b c"]);

        Assert.Equal(5, summary.TotalCount);
        Assert.Equal(4, summary.ByStatus["indexed"]);
        Assert.Equal($"/api/storages/{Id1}/summary?tags=bot%3Aa&tags=bot%3Ab%20c", server.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task Existence_is_a_direct_lookup_where_404_means_no_and_any_other_failure_is_thrown()
    {
        var (client, server) = Make();
        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.False(await client.StorageExistsAsync(Id1));

        server.Respond = _ => Json(new { id = Id1 });
        Assert.True(await client.AgentExistsAsync(Id1));

        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChannelExistsAsync(Id1));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    // ---- agent

    /// <summary>The agent PUT replaces the agent whole. A client that sent only the scopes it knew of would remove everyone else's.</summary>
    [Fact]
    public async Task An_agent_update_reads_the_current_settings_and_sends_back_every_scope_connector_and_storage_it_is_given()
    {
        var (client, server) = Make();
        server.Respond = request => request.Method == HttpMethod.Get
            ? Json(new { thinkingEffort = "high", webSearch = "auto" })
            : new HttpResponseMessage(HttpStatusCode.OK);
        var scopes = new[]
        {
            new AgentScope("sales", ["bot:sales"], "Be brief.", [Id2]),
            new AgentScope("hr", ["bot:hr"], null, []),
        };

        await client.UpdateAgentAsync(Id1, "acmeall", "", Id2, "instructions", [Id2], [Id1], scopes);

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(HttpMethod.Get, server.Requests[0].Method);
        using var body = JsonDocument.Parse(server.Requests[1].Body!);
        var root = body.RootElement;
        Assert.Equal("high", root.GetProperty("thinkingEffort").GetString());          // not reset to the server's "none"
        Assert.Equal("auto", root.GetProperty("webSearch").GetString());
        Assert.Equal([Id2], root.GetProperty("connectors").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal([Id1], root.GetProperty("storages").EnumerateArray().Select(e => e.GetGuid()));
        var sent = root.GetProperty("scopes").EnumerateArray().ToList();
        Assert.Equal(["sales", "hr"], sent.Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(["bot:sales"], sent[0].GetProperty("tags").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Be brief.", sent[0].GetProperty("instructions").GetString());
        Assert.Equal(JsonValueKind.Null, sent[1].GetProperty("instructions").ValueKind);
    }

    /// <summary>
    /// Found by reading the server: its update replaces the sub-agent mapping with the request's list, and an omitted list is an empty one.
    /// A client that said nothing about sub-agents would detach every one an operator had attached by hand.
    /// </summary>
    [Fact]
    public async Task An_agent_update_keeps_the_sub_agents_the_agent_already_has()
    {
        var (client, server) = Make();
        server.Respond = request => request.Method == HttpMethod.Get
            ? Json(new { thinkingEffort = "none", webSearch = "none", subAgents = new object[] { new { id = Id2, name = "helper", description = "" } } })
            : new HttpResponseMessage(HttpStatusCode.OK);

        await client.UpdateAgentAsync(Id1, "acmeall", "", null, "i", [], [], []);

        using var body = JsonDocument.Parse(server.Requests[1].Body!);
        Assert.Equal([Id2], body.RootElement.GetProperty("subAgents").EnumerateArray().Select(e => e.GetGuid()));
    }

    [Fact]
    public async Task An_agent_with_no_sub_agents_is_updated_with_an_empty_list_which_the_server_reads_as_none()
    {
        var (client, server) = Make();
        server.Respond = request => request.Method == HttpMethod.Get ? Json(new { }) : new HttpResponseMessage(HttpStatusCode.OK);

        await client.UpdateAgentAsync(Id1, "acmeall", "", null, "i", [], [], []);

        using var body = JsonDocument.Parse(server.Requests[1].Body!);
        Assert.Empty(body.RootElement.GetProperty("subAgents").EnumerateArray());
    }

    [Fact]
    public async Task An_agent_update_with_no_scopes_sends_an_empty_list_not_a_missing_one()
    {
        var (client, server) = Make();
        server.Respond = request => request.Method == HttpMethod.Get ? Json(new { }) : new HttpResponseMessage(HttpStatusCode.OK);

        await client.UpdateAgentAsync(Id1, "acmeall", "", null, "i", [], [], []);

        using var body = JsonDocument.Parse(server.Requests[1].Body!);
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("scopes").ValueKind);   // null would mean "do not change"
        Assert.Equal("none", body.RootElement.GetProperty("thinkingEffort").GetString());
    }

    [Fact]
    public async Task An_mcp_connector_is_sent_with_the_type_the_server_needs_to_read_its_config()
    {
        var (client, server) = Make();
        server.Respond = _ => Json(new { id = Id1, name = "c" });

        var id = await client.CreateMcpConnectorAsync("c", "d", "https://mcp.example/x", new Dictionary<string, string> { ["Authorization"] = "Bearer {{KEY}}" });

        Assert.Equal(Id1, id);
        using var body = JsonDocument.Parse(server.Requests[0].Body!);
        var config = body.RootElement.GetProperty("config");
        Assert.Equal("mcp", config.GetProperty("type").GetString());
        Assert.Equal("https://mcp.example/x", config.GetProperty("url").GetString());
        Assert.Equal("Bearer {{KEY}}", config.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public async Task A_web_channel_is_created_on_an_agent_with_the_web_type()
    {
        var (client, server) = Make();
        server.Respond = _ => Json(new { id = Id2 });

        Assert.Equal(Id2, await client.CreateWebChannelAsync("acmeall", Id1));

        using var body = JsonDocument.Parse(server.Requests[0].Body!);
        Assert.Equal(Id1, body.RootElement.GetProperty("agentId").GetGuid());
        Assert.Equal("web", body.RootElement.GetProperty("config").GetProperty("type").GetString());
    }

    // ---- token

    [Fact]
    public async Task A_channel_token_is_read_back()
    {
        var (client, server) = Make();
        var expires = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        server.Respond = _ => Json(new { token = "ve_abc", expiresAt = expires });

        var token = await client.IssueChannelTokenAsync(Id1);

        Assert.Equal(("ve_abc", expires), (token.Token, token.ExpiresAt));
        Assert.Equal(HttpMethod.Post, server.Requests[0].Method);
    }

    /// <summary>The body of a failed token request may contain a token; it is not put in an exception that gets logged.</summary>
    [Fact]
    public async Task A_failed_token_request_does_not_carry_the_response_body_in_its_message()
    {
        var (client, server) = Make();
        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("ve_SECRET-IN-BODY") };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.IssueChannelTokenAsync(Id1));

        Assert.DoesNotContain("SECRET-IN-BODY", ex.Message);
        Assert.Contains("502", ex.Message);
    }

    [Fact]
    public async Task Another_failure_carries_the_first_thousand_characters_of_the_response_body()
    {
        var (client, server) = Make();
        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(new string('x', 1500)) };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteAgentAsync(Id1));

        Assert.Contains(new string('x', 1000), ex.Message);
        Assert.DoesNotContain(new string('x', 1001), ex.Message);
        Assert.Contains("truncated", ex.Message);
    }

    // ---- files

    [Fact]
    public async Task An_upload_sends_the_file_under_its_utf8_name_with_one_tag_field_per_tag()
    {
        var (client, server) = Make();
        server.Respond = _ => Json(new[] { new { id = Id2, fileName = "x" } });
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        var id = await client.UploadFileAsync(Id1, "영업/계약서 1.pdf", ["bot:영업", "extra"], content);

        Assert.Equal(Id2, id);
        var request = Assert.Single(server.Requests);
        Assert.Equal($"/api/storages/{Id1}/files", request.PathAndQuery);
        Assert.Equal("영업/계약서 1.pdf", request.UploadedFileName);
        Assert.Equal([("tags", "bot:영업"), ("tags", "extra")], request.FormFields);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task An_upload_the_server_refuses_for_its_kind_or_size_is_a_skip_not_a_failure(HttpStatusCode status)
    {
        var (client, server) = Make();
        server.Respond = _ => new HttpResponseMessage(status) { Content = new StringContent("no") };
        await using var content = new MemoryStream([1]);

        var ex = await Assert.ThrowsAsync<VaultAiUnsupportedFileException>(() => client.UploadFileAsync(Id1, "a.bin", [], content));

        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task The_same_status_on_another_call_is_an_ordinary_failure()
    {
        var (client, server) = Make();
        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteStorageAsync(Id1));

        Assert.IsNotType<VaultAiUnsupportedFileException>(ex);
    }

    [Fact]
    public async Task A_delete_that_meets_a_conflict_says_the_resource_is_busy_and_a_retry_does_too()
    {
        var (client, server) = Make();
        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.Conflict);

        await Assert.ThrowsAsync<VaultAiConflictException>(() => client.DeleteFileAsync(Id1, Id2));
        await Assert.ThrowsAsync<VaultAiConflictException>(() => client.DeleteStorageAsync(Id1));
        await Assert.ThrowsAsync<VaultAiConflictException>(() => client.RetryFilePipelineAsync(Id1, Id2));
    }

    [Fact]
    public async Task A_conflict_on_a_create_is_an_ordinary_failure()
    {
        var (client, server) = Make();
        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.Conflict);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateStorageAsync("n", "d", null, null, null));

        Assert.IsNotType<VaultAiConflictException>(ex);
    }

    // ---- the time limit

    [Fact]
    public async Task A_call_that_outlasts_the_limit_is_a_timeout_not_the_callers_cancellation()
    {
        var (client, server) = Make(TimeSpan.FromMilliseconds(100));
        server.Respond = _ => throw new InvalidOperationException("unused");
        var slow = new SlowServer();
        var http = new HttpClient(slow) { BaseAddress = new Uri("https://vault.example/") };
        var timed = new FolderChatbotAdminClient(http, TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<VaultAiTimeoutException>(() => timed.ListModelsAsync());
    }

    [Fact]
    public async Task The_callers_own_cancellation_stays_a_cancellation()
    {
        var slow = new SlowServer();
        var timed = new FolderChatbotAdminClient(new HttpClient(slow) { BaseAddress = new Uri("https://vault.example/") }, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timed.ListModelsAsync(cts.Token));
    }

    private sealed class SlowServer : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
