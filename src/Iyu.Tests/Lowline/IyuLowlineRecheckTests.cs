using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Iyu.Lowline;
using Lowline;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// Holding answers back so the values people settle can be checked: the server draws, the server remembers what it held
/// back, and neither is something a request can claim or avoid.
/// </summary>
public sealed class IyuLowlineRecheckTests : IAsyncLifetime
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    private const string SiteForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]},{"name":"site","kind":"choice","choices":["north","south"]}]}}""";

    private const string Salt ="0123456789abcdef0123456789abcdef-installation-salt";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");
    private readonly OrderHook _hook = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app is null) return;
        await _app.Services.GetRequiredService<LowlineForms>().WhenTunedAsync(new LowlineScope("order", "plant-a"));
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    /// <summary>Starts a host whose every roll is <paramref name="roll"/> (0 draws every suggested field; 0.99 draws none).</summary>
    private async Task StartAsync(
        double roll, bool withSalt = true, int? maxPending = null, TimeProvider? time = null, IDistributedCache? cache = null,
        string form = OrderForm, ILoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        if (logs is not null) builder.Logging.AddProvider(logs);
        if (time is not null) builder.Services.AddSingleton(time);
        if (cache is not null) builder.Services.AddSingleton(cache);
        builder.Services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, LowlineTestAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ILowlineRoll>(new FixedRoll(roll));
        builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o =>
        {
            o.Engine.Form("order", form);
            if (withSalt) o.Settler.Salt = Salt;
            if (maxPending is { } limit) o.MaxPendingChecks = limit;
        });
        builder.Services.AddScoped<ILowlineFormHook>(_ => _hook);
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapIyuLowline();
        await _app.StartAsync();
        using (var scope = _app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();
        _client = _app.GetTestClient();

        // Forty acme orders settled as red, so the engine suggests red for an acme order.
        for (var i = 0; i < 40; i++)
        {
            _hook.Save($"seed{i}", "alice", "plant-a", "acme", "red");
            var seeded = await Send("/lowline/order/settle", $$"""{"id":"seed{{i}}","draft":null}""");
            Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        }

        await _app.Services.GetRequiredService<LowlineForms>().WhenTunedAsync(new LowlineScope("order", "plant-a"));
    }

    private Task<HttpResponseMessage> Send(string url, string json, string user = "alice")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Test-User", user);
        return _client.SendAsync(request);
    }

    private async Task<JsonElement> ColourProposal(string recordId = "new")
    {
        var response = await Send("/lowline/order/propose", "{\"id\":\"" + recordId + "\",\"values\":{\"customer\":\"acme\"}}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(p => p.GetProperty("field").GetString() == "colour");
    }

    private async Task<JsonElement> Stored(string id) =>
        JsonDocument.Parse((await _app.Services.GetRequiredService<ILowlineStore>().ReadRecordsAsync(new LowlineScope("order", "plant-a")))
            .Single(r => r.Id == id).Json).RootElement;

    private static bool HasSuggestion(JsonElement proposal) =>
        proposal.TryGetProperty("suggestion", out var s) && s.ValueKind == JsonValueKind.Object;

    private async Task SettleNew(string draft = "null")
    {
        _hook.Save("new", "alice", "plant-a", "acme", "red");
        var response = await Send("/lowline/order/settle", "{\"id\":\"new\",\"draft\":" + draft + "}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_field_that_is_drawn_has_its_answer_held_back_from_the_response()
    {
        await StartAsync(roll: 0);

        Assert.False(HasSuggestion(await ColourProposal()));
    }

    [Fact]
    public async Task A_field_that_is_not_drawn_is_answered_as_usual()
    {
        await StartAsync(roll: 0.99);

        var colour = await ColourProposal();

        Assert.True(HasSuggestion(colour));
        Assert.Equal("red", colour.GetProperty("suggestion").GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_record_settled_after_a_draw_is_recorded_as_a_check_with_its_probability()
    {
        await StartAsync(roll: 0);
        await ColourProposal();

        await SettleNew();

        var record = await Stored("new");
        Assert.Equal("Check", record.GetProperty("settlements").GetProperty("colour").GetString());
        Assert.Equal(0.05, record.GetProperty("probabilities").GetProperty("colour").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task What_the_draft_says_was_shown_for_a_held_field_does_not_replace_the_check()
    {
        await StartAsync(roll: 0);
        await ColourProposal();

        // The caller was shown nothing for colour, but says it was shown (and took) "blue".
        await SettleNew("""{"opened":{},"fields":{"colour":{"shown":{"value":"blue"}}}}""");

        var record = await Stored("new");
        Assert.Equal("Check", record.GetProperty("settlements").GetProperty("colour").GetString());
        // The answer that was held back is the server's, not the one the caller says it saw.
        Assert.Equal("red", record.GetProperty("suggested").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task A_record_settled_with_no_draw_is_not_a_check_whatever_the_draft_claims()
    {
        await StartAsync(roll: 0);

        await SettleNew("""{"opened":{},"fields":{"colour":{"held":0.5}}}""");

        var record = await Stored("new");
        Assert.NotEqual("Check", record.GetProperty("settlements").GetProperty("colour").GetString());
        Assert.Empty(record.GetProperty("probabilities").EnumerateObject());
    }

    [Fact]
    public async Task A_draw_is_used_once()
    {
        await StartAsync(roll: 0);
        await ColourProposal();
        await SettleNew();

        await SettleNew();

        Assert.NotEqual("Check", (await Stored("new")).GetProperty("settlements").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task A_later_proposal_that_holds_nothing_back_does_not_clear_an_earlier_draw()
    {
        await StartAsync(roll: 0);
        await ColourProposal();

        // Asking again with values that give the field nothing to suggest must not be a way to avoid the check.
        var response = await Send("/lowline/order/propose", """{"id":"new","values":{"customer":"globex"}}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SettleNew();

        Assert.Equal("Check", (await Stored("new")).GetProperty("settlements").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task A_later_proposal_that_draws_another_field_adds_to_the_earlier_draw()
    {
        await StartAsync(roll: 0);
        await ColourProposal();                     // customer is known: colour is drawn
        var second = await Send("/lowline/order/propose", """{"id":"new","values":{"colour":"red"}}""");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // colour is known: customer is drawn

        await SettleNew();

        var settlements = (await Stored("new")).GetProperty("settlements");
        Assert.Equal("Check", settlements.GetProperty("colour").GetString());
        Assert.Equal("Check", settlements.GetProperty("customer").GetString());
    }

    [Fact]
    public async Task A_settle_the_engine_refuses_does_not_use_up_the_draw()
    {
        await StartAsync(roll: 0);
        await ColourProposal();
        // The stored record holds a colour the form does not have: the engine refuses it, after the draw was read.
        _hook.Save("new", "alice", "plant-a", "acme", "green");
        var refused = await Send("/lowline/order/settle", """{"id":"new","draft":null}""");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        await SettleNew();

        Assert.Equal("Check", (await Stored("new")).GetProperty("settlements").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task A_settle_with_a_draft_that_cannot_be_read_does_not_use_up_the_draw()
    {
        await StartAsync(roll: 0);
        await ColourProposal();
        _hook.Save("new", "alice", "plant-a", "acme", "red");
        var refused = await Send("/lowline/order/settle", """{"id":"new","draft":"not an object"}""");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        await SettleNew();

        Assert.Equal("Check", (await Stored("new")).GetProperty("settlements").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task A_proposal_made_on_values_the_saved_record_never_had_is_still_a_check_but_plants_no_answer()
    {
        await StartAsync(roll: 0);
        await ColourProposal();
        // The proposal rested on customer = acme; the record that was saved says globex.
        _hook.Save("new", "alice", "plant-a", "globex", "red");

        var response = await Send("/lowline/order/settle", """{"id":"new","draft":null}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await Stored("new");
        // Disagreeing with the proposal is not a way out of the check ...
        Assert.Equal("Check", record.GetProperty("settlements").GetProperty("colour").GetString());
        Assert.Equal(0.05, record.GetProperty("probabilities").GetProperty("colour").GetDouble(), precision: 6);
        // ... and the answer held back for the context that was never saved is not written into the record.
        Assert.False(record.TryGetProperty("suggested", out var suggested) && suggested.TryGetProperty("colour", out _),
            record.GetRawText());
    }

    [Fact]
    public async Task A_proposal_made_on_fewer_values_than_the_record_ends_up_with_is_still_a_check_but_plants_no_answer()
    {
        await StartAsync(roll: 0, form: SiteForm);
        // Asked with the customer only: the answer held back for colour rests on a narrower question than the record answers.
        await ColourProposal();
        _hook.Save("new", "alice", "plant-a", "acme", "red", site: "north");

        var response = await Send("/lowline/order/settle", """{"id":"new","draft":null}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await Stored("new");
        Assert.Equal("Check", record.GetProperty("settlements").GetProperty("colour").GetString());
        Assert.False(record.TryGetProperty("suggested", out var suggested) && suggested.TryGetProperty("colour", out _),
            record.GetRawText());
    }

    [Fact]
    public async Task A_settler_at_the_limit_is_refused_instead_of_answered_without_the_check()
    {
        await StartAsync(roll: 0, maxPending: 2);
        await ColourProposal("r1");
        await ColourProposal("r2");

        var third = await Send("/lowline/order/propose", """{"id":"r3","values":{"customer":"acme"}}""");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        // A record that already has its place is not a new one.
        Assert.False(HasSuggestion(await ColourProposal("r1")));
    }

    [Fact]
    public async Task A_refused_proposal_is_logged_for_the_host_without_the_record_or_the_person()
    {
        var logs = new CollectedLogs();
        await StartAsync(roll: 0, maxPending: 1, logs: logs);
        await ColourProposal("first-record");

        var refused = await Send("/lowline/order/propose", """{"id":"second-record","values":{"customer":"acme"}}""");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var warning = Assert.Single(logs.Warnings, line => line.Contains("refused a proposal", StringComparison.Ordinal));
        Assert.Contains("order", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("second-record", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(IyuLowlineSettlerIdentity.Pseudonym(Salt, "account-alice"), warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_settled_record_gives_its_place_back()
    {
        await StartAsync(roll: 0, maxPending: 1);
        await ColourProposal("r1");
        _hook.Save("r1", "alice", "plant-a", "acme", "red");
        Assert.Equal(HttpStatusCode.OK, (await Send("/lowline/order/settle", """{"id":"r1","draft":null}""")).StatusCode);

        Assert.False(HasSuggestion(await ColourProposal("r2")));
    }

    [Fact]
    public async Task A_place_lapses_when_the_draw_does()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        await StartAsync(roll: 0, maxPending: 1, time: time);
        await ColourProposal("r1");
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await Send("/lowline/order/propose", """{"id":"r2","values":{"customer":"acme"}}""")).StatusCode);

        time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1));

        Assert.False(HasSuggestion(await ColourProposal("r2")));
    }

    [Fact]
    public async Task One_settler_at_the_limit_does_not_stop_another()
    {
        await StartAsync(roll: 0, maxPending: 1);
        await ColourProposal("r1");

        var other = await Send("/lowline/order/propose", """{"id":"r2","values":{"customer":"acme"}}""", user: "bob");

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task A_cache_that_cannot_keep_the_draw_fails_the_proposal_rather_than_answering_without_the_check()
    {
        var cache = new FlakyCache();
        await StartAsync(roll: 0, cache: cache);
        cache.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(() => Send("/lowline/order/propose", """{"id":"new","values":{"customer":"acme"}}"""));
    }

    [Fact]
    public async Task A_cache_that_cannot_be_read_fails_the_settle_rather_than_settling_without_the_check()
    {
        var cache = new FlakyCache();
        await StartAsync(roll: 0, cache: cache);
        await ColourProposal();
        _hook.Save("new", "alice", "plant-a", "acme", "red");
        cache.FailReads = true;

        await Assert.ThrowsAsync<IOException>(() => Send("/lowline/order/settle", """{"id":"new","draft":null}"""));

        cache.FailReads = false;
        Assert.Equal(HttpStatusCode.OK, (await Send("/lowline/order/settle", """{"id":"new","draft":null}""")).StatusCode);
        Assert.Equal("Check", (await Stored("new")).GetProperty("settlements").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task Requests_of_one_settler_that_arrive_together_cannot_slip_past_the_limit()
    {
        var cache = new FlakyCache();
        await StartAsync(roll: 0, maxPending: 3, cache: cache);
        cache.ReadDelay = TimeSpan.FromMilliseconds(40);

        var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Send("/lowline/order/propose", "{\"id\":\"together" + i + "\",\"values\":{\"customer\":\"acme\"}}")));

        cache.ReadDelay = TimeSpan.Zero;
        Assert.Equal(3, replies.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(9, replies.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(3, cache.Keys.Count(key => key.StartsWith("iyu-lowline/check/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_cache_is_given_a_digest_of_the_record_id_not_the_id()
    {
        var cache = new FlakyCache();
        await StartAsync(roll: 0, cache: cache);
        var id = new string('q', 5000);

        var proposed = await Send("/lowline/order/propose", "{\"id\":\"" + id + "\",\"values\":{\"customer\":\"acme\"}}");

        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        Assert.NotEmpty(cache.Keys);
        Assert.All(cache.Keys, key => Assert.True(key.Length < 200, key.Length.ToString()));
        Assert.DoesNotContain(cache.Texts, text => text.Contains("qqqqqqqq", StringComparison.Ordinal));

        // The draw is found again from the same digest at settle.
        _hook.Save(id, "alice", "plant-a", "acme", "red");
        Assert.Equal(HttpStatusCode.OK, (await Send("/lowline/order/settle", "{\"id\":\"" + id + "\",\"draft\":null}")).StatusCode);
        Assert.Equal("Check", (await Stored(id)).GetProperty("settlements").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task A_settle_whose_cleanup_fails_leaves_the_draw_so_the_retry_is_still_checked()
    {
        var cache = new FlakyCache();
        await StartAsync(roll: 0, cache: cache);
        await ColourProposal("other");   // a second pending draw, so the settler's list is rewritten rather than dropped
        await ColourProposal();
        _hook.Save("new", "alice", "plant-a", "acme", "red");
        cache.FailWrites = true;

        // The record is settled and learned; clearing its place in the list fails after that.
        await Assert.ThrowsAsync<IOException>(() => Send("/lowline/order/settle", """{"id":"new","draft":null}"""));

        cache.FailWrites = false;
        Assert.Equal(HttpStatusCode.OK, (await Send("/lowline/order/settle", """{"id":"new","draft":null}""")).StatusCode);
        Assert.Equal("Check", (await Stored("new")).GetProperty("settlements").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task Without_a_salt_nothing_is_held_back_even_when_every_roll_would_draw()
    {
        await StartAsync(roll: 0, withSalt: false);

        Assert.True(HasSuggestion(await ColourProposal()));
    }

    [Fact]
    public async Task The_roll_is_stable_for_one_field_of_one_record_and_differs_between_records()
    {
        var roll = new HmacLowlineRoll();

        var first = roll.Roll(Salt, "order", "plant-a", "r1", "colour");

        Assert.Equal(first, roll.Roll(Salt, "order", "plant-a", "r1", "colour"));
        Assert.NotEqual(first, roll.Roll(Salt, "order", "plant-a", "r2", "colour"));
        Assert.NotEqual(first, roll.Roll(Salt + "x", "order", "plant-a", "r1", "colour"));
        Assert.InRange(first, 0.0, 1.0);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_rolls_are_spread_over_the_unit_interval()
    {
        var roll = new HmacLowlineRoll();
        var drawn = Enumerable.Range(0, 2000).Count(i => roll.Roll(Salt, "order", "plant-a", $"r{i}", "colour") < 0.05);

        // About one in twenty (100 of 2,000); a wide band keeps this from depending on the salt.
        Assert.InRange(drawn, 60, 150);
        await Task.CompletedTask;
    }

    private sealed class FixedRoll(double value) : ILowlineRoll
    {
        public double Roll(string salt, string form, string space, string recordId, string field) => value;
    }

    /// <summary>A logger provider that keeps what is written to it.</summary>
    private sealed class CollectedLogs : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => [.. _warnings];

        public ILogger CreateLogger(string categoryName) => new Sink(_warnings);

        public void Dispose()
        {
        }

        private sealed class Sink(System.Collections.Concurrent.ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    /// <summary>A cache that works until it is told not to — the shared cache of a host with a node or a network that goes down.</summary>
    private sealed class FlakyCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _entries = [];

        public bool FailReads { get; set; }

        /// <summary>Whether storing an entry fails (removing one does not).</summary>
        public bool FailWrites { get; set; }

        /// <summary>How long a read takes — long enough for requests that arrive together to read before any of them writes.</summary>
        public TimeSpan ReadDelay { get; set; }

        /// <summary>Every key the cache holds.</summary>
        public IReadOnlyList<string> Keys { get { lock (_entries) return [.. _entries.Keys]; } }

        /// <summary>Every value the cache holds, as text.</summary>
        public IReadOnlyList<string> Texts { get { lock (_entries) return [.. _entries.Values.Select(Encoding.UTF8.GetString)]; } }

        public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            if (FailReads) throw new IOException("the cache is unreachable");
            if (ReadDelay > TimeSpan.Zero) await Task.Delay(ReadDelay, token);
            lock (_entries) return _entries.GetValueOrDefault(key);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            SetAsync(key, value, options).GetAwaiter().GetResult();

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            if (FailWrites) throw new IOException("the cache is unreachable");
            lock (_entries) _entries[key] = value;
            return Task.CompletedTask;
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            lock (_entries) _entries.Remove(key);
            return Task.CompletedTask;
        }
    }
}
