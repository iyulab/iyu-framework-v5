using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Iyu.UBoard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Iyu.Tests.Server.OData;

namespace Iyu.Tests.UBoard;

/// <summary>
/// A fact that runs only when a live model service was named in the environment (<c>UBOARD_LIVE_MODEL_BASE_URL</c> and
/// <c>UBOARD_LIVE_MODEL_API_KEY</c>). Without them it is reported as skipped with a line that says so — never as a pass.
/// </summary>
public sealed class LiveModelFactAttribute : FactAttribute
{
    public LiveModelFactAttribute()
    {
        if (LiveModel.BaseUrl is null || LiveModel.ApiKey is null)
            Skip = "SKIPPED: live model environment not set (UBOARD_LIVE_MODEL_BASE_URL, UBOARD_LIVE_MODEL_API_KEY)";
    }
}

internal static class LiveModel
{
    public static string? BaseUrl => Read("UBOARD_LIVE_MODEL_BASE_URL");

    public static string? ApiKey => Read("UBOARD_LIVE_MODEL_API_KEY");

    private static string? Read(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}

/// <summary>
/// The model proxy against a real OpenAI-compatible service: list its models, then send a short completion through the host's own
/// route. These calls cost something and load a shared service, so they stay few and short, and a failure says what failed — never the
/// address or the key.
/// </summary>
[Trait("Category", "ModelLive")]
public sealed class ModelLiveTests(ITestOutputHelper output)
{
    private const string Chat = "/uboard/model/chat/completions";

    // One lookup for the whole class: the list of models is asked for once. The address the environment names may be the service's
    // root or its OpenAI-style prefix, so the list is asked for at the address and then under the usual prefixes; the first that
    // answers with a list is the base the proxy is given. Which one it was is not reported.
    private static readonly Lazy<Task<(string Base, string Model)>> Service = new(DiscoverAsync);

    private static async Task<(string Base, string Model)> DiscoverAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var root = LiveModel.BaseUrl!.TrimEnd('/');
        var refusals = new List<int>();
        foreach (var candidate in new[] { root, root + "/v1", root + "/v1-openai" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LiveModel.ApiKey);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request);
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
            {
                throw new InvalidOperationException("The live model service could not be reached to list its models (" + failure.GetType().Name + ").");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    refusals.Add((int)response.StatusCode);
                    continue;
                }

                var listed = (JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject)?["data"] as JsonArray ?? [];
                // A chat model, not an embedding, ranking, speech or image one.
                var name = listed.Select(m => (string?)m?["id"]).OfType<string>()
                    .FirstOrDefault(id => !new[] { "embed", "rerank", "whisper", "tts", "bge", "clip", "image", "stable-diffusion" }
                        .Any(skip => id.Contains(skip, StringComparison.OrdinalIgnoreCase)));
                return name is null ? throw new InvalidOperationException("The live service listed no chat model.") : (candidate, name);
            }
        }

        throw new InvalidOperationException("No address listed the live service's models; they answered " + string.Join(", ", refusals) + ".");
    }

    private static async Task<WebApplication> StartProxyAsync((string Base, string Model) service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddIyuUBoard(o => o.Model(m =>
        {
            m.BaseUrl = new Uri(service.Base);
            m.ApiKey = LiveModel.ApiKey;
            m.ModelName = service.Model;
            m.MaxOutputTokens = 60;
            m.Timeout = TimeSpan.FromSeconds(90);
        }));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIyuUBoard();
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Ask(JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Chat) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Test-Perm", "signed-in");
        return request;
    }

    [LiveModelFact]
    public async Task The_services_models_can_be_listed_and_a_chat_model_found()
    {
        var (_, name) = await Service.Value;

        Assert.False(string.IsNullOrWhiteSpace(name));
        output.WriteLine("LIVE: a chat model was found in the service's list.");
    }

    [LiveModelFact]
    public async Task A_short_completion_through_the_proxy_comes_back_and_the_key_is_nowhere_in_it()
    {
        await using var app = await StartProxyAsync(await Service.Value);
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with the single word: ok" }),
            ["max_tokens"] = 60,
        };

        using var response = await app.GetTestServer().CreateClient().SendAsync(Ask(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"The proxy answered {(int)response.StatusCode} for a short completion.");
        var content = (string?)((JsonNode.Parse(text) as JsonObject)?["choices"]?[0]?["message"]?["content"]);
        Assert.False(string.IsNullOrWhiteSpace(content), "The completion carried no content.");
        Assert.DoesNotContain(LiveModel.ApiKey!, text, StringComparison.Ordinal);
        Assert.DoesNotContain(LiveModel.ApiKey!, string.Join('\n', response.Headers.Select(h => string.Join(',', h.Value))), StringComparison.Ordinal);
        output.WriteLine("LIVE: a short completion came back through the proxy with content and without the key.");
    }

    [LiveModelFact]
    public async Task Whether_the_service_calls_a_tool_is_recorded_and_never_fails_the_run()
    {
        await using var app = await StartProxyAsync(await Service.Value);
        var body = JsonNode.Parse("""
            {"messages":[{"role":"user","content":"What time is it in UTC? Use the tool."}],
             "tools":[{"type":"function","function":{"name":"get_time","description":"Returns the current time in UTC.","parameters":{"type":"object","properties":{}}}}],
             "tool_choice":"auto","max_tokens":60}
            """)!.AsObject();

        string fact;
        using (var response = await app.GetTestServer().CreateClient().SendAsync(Ask(body)))
        {
            var calls = response.StatusCode == HttpStatusCode.OK
                ? (JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject)?["choices"]?[0]?["message"]?["tool_calls"] as JsonArray
                : null;
            fact = response.StatusCode != HttpStatusCode.OK
                ? $"tool-calling: the service refused the request ({(int)response.StatusCode})"
                : calls is { Count: > 0 } ? "tool-calling: ok (the service answered with a tool call)" : "tool-calling: not observed (the service answered without a tool call)";
        }

        output.WriteLine("LIVE " + fact);
        // The console shows no output of a passing test, so the one fact worth reporting is also left where the run can be asked.
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "uboard-live-tool-calling.txt"), fact);
    }
}
