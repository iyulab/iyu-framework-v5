using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Iyu.Tests.UBoard;

/// <summary>
/// A model service that plays back replies written ahead of time: an OpenAI-compatible chat endpoint on a free loopback port that
/// answers each request with the next scripted reply (as a stream, as the assistant asks) and keeps every request it was sent. No key,
/// no network beyond this machine, and nothing the test did not write down.
/// </summary>
public sealed class ScriptedModelService : IAsyncDisposable
{
    /// <summary>One thing the service does when asked: say words, ask for tools, or refuse.</summary>
    public sealed record Reply(string? Text = null, (string Id, string Name, string Arguments)[]? Calls = null, int? Status = null, TimeSpan? Delay = null)
    {
        public static Reply Say(string text) => new(Text: text);

        public static Reply Ask(params (string Id, string Name, string Arguments)[] calls) => new(Calls: calls);

        public static Reply Refuse(int status) => new(Status: status);

        public static Reply Slow(TimeSpan delay, string text) => new(Text: text, Delay: delay);
    }

    /// <summary>What the service was sent.</summary>
    public sealed record Request(string Method, string Path, JsonNode? Body, IReadOnlyDictionary<string, string> Headers);

    private readonly WebApplication _app;
    private readonly Queue<Reply> _script = new();
    private readonly List<Request> _requests = [];
    private readonly object _gate = new();

    private ScriptedModelService(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>The address to give the assistant.</summary>
    public string BaseUrl { get; }

    /// <summary>The requests that asked for a reply, in order.</summary>
    public IReadOnlyList<Request> Requests
    {
        get
        {
            lock (_gate) return [.. _requests.Where(r => r.Method == "POST")];
        }
    }

    /// <summary>How many scripted replies are still unused.</summary>
    public int Remaining
    {
        get
        {
            lock (_gate) return _script.Count;
        }
    }

    /// <summary>Adds replies to say next, in order.</summary>
    public ScriptedModelService Script(params Reply[] replies)
    {
        lock (_gate)
            foreach (var reply in replies) _script.Enqueue(reply);
        return this;
    }

    /// <summary>Starts the service.</summary>
    public static async Task<ScriptedModelService> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        ScriptedModelService? service = null;
        app.Map("/{**path}", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync();
            var body = raw.Length > 0 ? JsonNode.Parse(raw) : null;
            var headers = http.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());

            Reply? reply = null;
            lock (service!._gate)
            {
                service._requests.Add(new Request(http.Request.Method, http.Request.Path.Value ?? "", body, headers));
                if (http.Request.Method == "POST") service._script.TryDequeue(out reply);
            }

            if (http.Request.Method != "POST")
            {
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync("""{"data":[{"id":"scripted"}]}""", http.RequestAborted);
                return;
            }

            if (reply is null)
            {
                http.Response.StatusCode = 500;
                await http.Response.WriteAsync("""{"error":"the scripted model has nothing left to say"}""", http.RequestAborted);
                return;
            }

            if (reply.Delay is { } delay) await Task.Delay(delay, http.RequestAborted);
            if (reply.Status is { } status)
            {
                http.Response.StatusCode = status;
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync("""{"error":{"message":"the service says no, at https://models.internal.example, with key sk-secret"}}""", http.RequestAborted);
                return;
            }

            var message = new JsonObject { ["role"] = "assistant" };
            if (reply.Text is not null) message["content"] = reply.Text;
            if (reply.Calls is { } calls)
                message["tool_calls"] = new JsonArray([.. calls.Select((c, index) => (JsonNode)new JsonObject
                {
                    ["index"] = index,
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                })]);

            string Chunk(JsonObject delta, string? finish, bool usage = false) =>
                "data: " + new JsonObject
                {
                    ["id"] = "scripted",
                    ["object"] = "chat.completion.chunk",
                    ["model"] = "scripted",
                    ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
                    ["usage"] = usage ? new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 } : null,
                }.ToJsonString() + "\n\n";

            http.Response.ContentType = "text/event-stream";
            await http.Response.WriteAsync(
                Chunk(message, null) + Chunk(new JsonObject(), reply.Calls is null ? "stop" : "tool_calls", usage: true) + "data: [DONE]\n\n",
                http.RequestAborted);
        });
        await app.StartAsync();
        service = new ScriptedModelService(app, app.Urls.First());
        return service;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
