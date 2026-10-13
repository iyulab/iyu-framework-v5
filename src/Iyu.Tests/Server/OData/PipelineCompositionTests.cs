using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iyu.MainServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.OData;

/// <summary>
/// <c>UseIyuMainServer</c> is three calls — the error responses, routing, the endpoints — and a host that
/// needs its own middleware at a particular place can make them itself and get the same runtime.
/// </summary>
/// <remarks>
/// The pieces have to be public for that to hold. While they were not, a host composing its pipeline by hand
/// re-created them from reading the source, and the one it missed — the error body for the OData route's
/// body-less failures — failed nothing: the statuses were right and the bodies were empty.
/// </remarks>
public class PipelineCompositionTests
{
    private const string Ledgers = "TallyBooks";

    /// <summary>What the request's endpoint was when the host's own middleware ran, per request path.</summary>
    private sealed class EndpointSeen
    {
        public Dictionary<string, string?> ByPath { get; } = new();
    }

    private static async Task<(WebApplication App, EndpointSeen Seen)> StartAsync(Action<WebApplication, RequestDelegate?> compose)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        var dbName = "pipeline-" + Guid.NewGuid().ToString("N");
        builder.Services.AddIyuMainServer<TallyBookContext>(
            configureDb: db => db.UseInMemoryDatabase(dbName),
            configure: options =>
            {
                options.ControllerAssemblies.Add(typeof(TallyBooksController).Assembly);
                options.ODataModel.AddEntityPair<TallyBookExt, TallyBook>(Ledgers);
            });
        var seen = new EndpointSeen();
        builder.Services.AddSingleton(seen);

        var app = builder.Build();
        compose(app, null);
        await app.StartAsync();
        return (app, seen);
    }

    /// <summary>The host's middleware: records the endpoint routing chose for the request, then goes on.</summary>
    private static void UseEndpointRecorder(WebApplication app) =>
        app.Use(async (context, next) =>
        {
            context.RequestServices.GetRequiredService<EndpointSeen>().ByPath[context.Request.Path] =
                context.GetEndpoint()?.DisplayName;
            await next(context);
        });

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length == 0) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    /// <summary>
    /// Middleware a host adds after <c>UseIyuMainServer()</c> runs after routing has chosen the endpoint and
    /// before the endpoint executes — the place authentication, authorization and middleware that reads the
    /// endpoint belong. A host that only needs that place does not have to compose the pipeline by hand.
    /// </summary>
    [Fact]
    public async Task Middleware_added_after_UseIyuMainServer_runs_between_routing_and_the_endpoint()
    {
        var (app, seen) = await StartAsync((a, _) =>
        {
            a.UseIyuMainServer();
            UseEndpointRecorder(a);
        });
        await using var _ = app;
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/$data/" + Ledgers);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(seen.ByPath["/$data/" + Ledgers]);              // routing had already chosen the endpoint
    }

    /// <summary>
    /// Composed by hand from the two named pieces, with the host's middleware between routing and the endpoints:
    /// the sets are served, and the OData route's body-less failures carry the error object.
    /// </summary>
    [Theory]
    [InlineData("GET", "/$data/Nope", HttpStatusCode.NotFound, "NotFound")]
    [InlineData("PUT", "/$data/" + Ledgers, HttpStatusCode.MethodNotAllowed, "MethodNotAllowed")]
    public async Task A_pipeline_composed_from_the_named_pieces_answers_like_UseIyuMainServer(
        string method, string path, HttpStatusCode status, string code)
    {
        var (app, seen) = await StartAsync((a, _) =>
        {
            a.UseIyuErrorResponses();
            a.UseRouting();
            UseEndpointRecorder(a);
            a.MapIyuMainServer();
        });
        await using var _ = app;
        using var client = app.GetTestClient();

        using var served = await client.GetAsync("/$data/" + Ledgers);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.NotNull(seen.ByPath["/$data/" + Ledgers]);

        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "PUT" ? JsonContent.Create(new { Title = "x" }) : null,
        };
        using var failed = await client.SendAsync(request);

        Assert.Equal(status, failed.StatusCode);
        Assert.Equal(code, await ErrorCodeAsync(failed));
    }

    /// <summary>
    /// The control: the same hand-composed pipeline without <c>UseIyuErrorResponses()</c> returns the same
    /// status with nothing in the body. That is the whole of the difference, and nothing else reports it.
    /// </summary>
    [Fact]
    public async Task Without_the_error_responses_piece_the_same_failure_has_no_body()
    {
        var (app, _) = await StartAsync((a, _) =>
        {
            a.UseRouting();
            a.MapIyuMainServer();
        });
        await using var __ = app;
        using var client = app.GetTestClient();

        using var failed = await client.GetAsync("/$data/Nope");

        Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);
        Assert.Null(await ErrorCodeAsync(failed));
    }
}
