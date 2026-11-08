using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Iyu.VaultAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// A call to the vault-ai server runs under a time limit — <c>VaultAi:RequestTimeout</c> by default, or the one the caller
/// passes with the call — and a call that outruns it says so with <see cref="VaultAiTimeoutException"/>, apart from the
/// caller's own cancellation. Every timing test asserts how long the call took: the client's own default limit is far
/// longer than the one under test, so a limit that did not apply would show as a slow test, not a wrong answer.
/// </summary>
public sealed class VaultAiCallDeadlineTests : IAsyncLifetime
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
    // The server never answers within the test: whatever ends the call has to be a limit or a cancellation.
    private static readonly TimeSpan Stuck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotLongerThan = TimeSpan.FromSeconds(10);

    private WebApplication _app = default!;
    private string _url = default!;
    private TimeSpan _serverDelay = Stuck;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.MapPost("/api/agents/{id}/messages", async (HttpContext ctx) =>
        {
            await Task.Delay(_serverDelay, ctx.RequestAborted);
            return Results.Json(new
            {
                message = new { content = new[] { new { type = "text", text = "ok" } } },
                output = new { answer = "ok" },
            });
        });
        await _app.StartAsync();
        _url = _app.Urls.Single();
    }

    public Task DisposeAsync() => _app.DisposeAsync().AsTask();

    private VaultAiClient Client(TimeSpan requestTimeout) => new(Options.Create(new VaultAiSettings
    {
        Url = _url,
        Token = "t",
        RequestTimeout = requestTimeout,
    }));

    [Fact]
    public async Task A_call_past_the_default_limit_fails_with_a_timeout_not_a_cancellation()
    {
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<VaultAiTimeoutException>(
            () => Client(Short).GetMessageAsync(Guid.NewGuid(), "hi"));

        Assert.Equal(Short, ex.Timeout);
        Assert.IsNotAssignableFrom<OperationCanceledException>(ex);
        Assert.True(watch.Elapsed < NotLongerThan, $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task The_callers_own_cancellation_stays_a_cancellation()
    {
        using var cts = new CancellationTokenSource(Short);
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(TimeSpan.FromMinutes(5)).GetMessageAsync(Guid.NewGuid(), "hi", cts.Token));

        Assert.True(watch.Elapsed < NotLongerThan, $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_shorter_limit_passed_with_the_call_wins_over_a_long_default()
    {
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<VaultAiTimeoutException>(
            () => Client(TimeSpan.FromMinutes(5)).GetMessageAsync(Guid.NewGuid(), "hi", Short));

        Assert.Equal(Short, ex.Timeout);
        Assert.True(watch.Elapsed < NotLongerThan, $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_longer_limit_passed_with_the_call_lets_it_outlast_a_short_default()
    {
        _serverDelay = TimeSpan.FromMilliseconds(900);

        var text = await Client(Short).GetMessageAsync(Guid.NewGuid(), "hi", TimeSpan.FromSeconds(20));

        Assert.Equal("ok", text);
    }

    [Fact]
    public async Task The_structured_call_obeys_the_same_limits()
    {
        var schema = JsonNode.Parse("""{"type":"object"}""")!;
        var watch = Stopwatch.StartNew();

        var byDefault = await Assert.ThrowsAsync<VaultAiTimeoutException>(
            () => Client(Short).GetStructuredMessageAsync(Guid.NewGuid(), "hi", schema));
        var perCall = await Assert.ThrowsAsync<VaultAiTimeoutException>(
            () => Client(TimeSpan.FromMinutes(5)).GetStructuredMessageAsync(Guid.NewGuid(), "hi", schema, Short));

        Assert.Equal(Short, byDefault.Timeout);
        Assert.Equal(Short, perCall.Timeout);
        Assert.True(watch.Elapsed < NotLongerThan, $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5000)]
    public async Task A_limit_that_is_not_greater_than_zero_is_refused(int milliseconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Client(TimeSpan.FromMinutes(5)).GetMessageAsync(
                Guid.NewGuid(), "hi", TimeSpan.FromMilliseconds(milliseconds)));
    }

    /// <summary>A client written before the limit overloads existed: it only has the two original methods.</summary>
    private sealed class StuckUntilCancelled : IVaultAiClient
    {
        public CancellationToken Seen;

        public async Task<string> GetMessageAsync(Guid agentId, string prompt, CancellationToken ct = default)
        {
            Seen = ct;
            await Task.Delay(Stuck, ct);
            return "late";
        }

        public async Task<JsonNode> GetStructuredMessageAsync(Guid agentId, string prompt, JsonNode outputSchema,
            IReadOnlyList<VaultAiImage>? images = null, CancellationToken ct = default)
        {
            await Task.Delay(Stuck, ct);
            return new JsonObject();
        }
    }

    [Fact]
    public async Task Another_implementation_gets_the_limit_overloads_without_changing()
    {
        var client = new StuckUntilCancelled();
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<VaultAiTimeoutException>(
            () => ((IVaultAiClient)client).GetMessageAsync(Guid.NewGuid(), "hi", Short));

        Assert.Equal(Short, ex.Timeout);
        Assert.True(client.Seen.IsCancellationRequested);
        Assert.True(watch.Elapsed < NotLongerThan, $"took {watch.Elapsed}");
    }

    private static IServiceProvider Provider(string requestTimeout)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VaultAi:Url"] = "https://vault.example.test",
                ["VaultAi:Token"] = "t",
                ["VaultAi:RequestTimeout"] = requestTimeout,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddVaultAiReports(config);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:05")]
    public void A_request_timeout_that_is_not_greater_than_zero_fails_validation_naming_the_key(string configured)
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => Provider(configured).GetRequiredService<IOptions<VaultAiSettings>>().Value);

        Assert.Contains("VaultAi:RequestTimeout", ex.Message);
        Assert.DoesNotContain(configured, ex.Message);
    }

    [Fact]
    public void The_request_timeout_defaults_to_the_ten_minutes_the_client_always_had()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), new VaultAiSettings().RequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1.5),
            Provider("00:01:30").GetRequiredService<IOptions<VaultAiSettings>>().Value.RequestTimeout);
    }
}
