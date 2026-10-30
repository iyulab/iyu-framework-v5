using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iyu.Lowline;
using Iyu.MainServer.Identity;
using Lowline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// The id the engine keeps for whoever settled a record: opaque, stable, never the account id, and never taken from the
/// request. It is written into every record, so the algorithm is pinned here — changing it re-attributes history.
/// </summary>
public sealed class IyuLowlineSettlerIdentityTests : IAsyncLifetime
{
    private const string OrderForm =
        """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""";

    private const string Salt = "0123456789abcdef0123456789abcdef-installation-salt";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-{Guid.NewGuid():N}.db");
    private readonly OrderHook _hook = new();
    private ServiceProvider _provider = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is null) return;
        await _provider.GetRequiredService<LowlineForms>().WhenTunedAsync(new LowlineScope("order", "plant-a"));
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private void Build(Action<IyuLowlineSettlerOptions>? settler = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o =>
        {
            o.Engine.Form("order", OrderForm);
            settler?.Invoke(o.Settler);
        });
        services.AddScoped<ILowlineFormHook>(_ => _hook);
        _provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<LowlineTestDb>().Database.EnsureCreated();
    }

    private static ClaimsPrincipal Person(string name, string? accountId = null, params Claim[] more)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        if (accountId is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, accountId));
        claims.AddRange(more);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private async Task<IyuLowlineSettleOutcome> Settle(string id, ClaimsPrincipal user, string? draft = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IyuLowlineSettleService>().SettleAsync("order", id, draft, user);
    }

    private async Task<JsonElement> Stored(string id) =>
        JsonDocument.Parse((await _provider.GetRequiredService<ILowlineStore>().ReadRecordsAsync(new LowlineScope("order", "plant-a")))
            .Single(r => r.Id == id).Json).RootElement;

    private static string Expected(string salt, string accountId)
    {
        // The algorithm, written out independently of the implementation: HKDF-SHA256 (purpose "iyu-lowline/settler")
        // from the salt, then HMAC-SHA256 of the account id, lower-case hex.
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(salt), 32, info: Encoding.ASCII.GetBytes("iyu-lowline/settler"));
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant();
    }

    [Fact]
    public async Task A_person_is_recorded_as_the_pinned_pseudonym_of_their_account_id()
    {
        Build(s => s.Salt = Salt);
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        await Settle("r1", Person("alice", accountId: "user-42"));

        var settledBy = (await Stored("r1")).GetProperty("settled_by");
        Assert.Equal(Expected(Salt, "user-42"), settledBy.GetProperty("colour").GetString());
        Assert.Equal(Expected(Salt, "user-42"), settledBy.GetProperty("customer").GetString());
    }

    [Fact]
    public async Task The_pseudonym_does_not_contain_the_account_id_and_differs_by_account_and_by_salt()
    {
        Assert.DoesNotContain("user-42", Expected(Salt, "user-42"));
        Assert.NotEqual(Expected(Salt, "user-42"), Expected(Salt, "user-43"));
        Assert.NotEqual(Expected(Salt, "user-42"), Expected(Salt + "x", "user-42"));
        Assert.Equal(Expected(Salt, "user-42"), IyuLowlineSettleService.Pseudonym(Salt, "user-42"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_draft_naming_another_settler_does_not_win_over_the_callers_own()
    {
        Build(s => s.Salt = Salt);
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        await Settle("r1", Person("alice", accountId: "user-42"), draft: """{"opened":{},"fields":{},"by":"user-1"}""");

        Assert.Equal(Expected(Salt, "user-42"), (await Stored("r1")).GetProperty("settled_by").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task Without_a_salt_no_settler_is_recorded()
    {
        Build();
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        await Settle("r1", Person("alice", accountId: "user-42"));

        Assert.False((await Stored("r1")).TryGetProperty("settled_by", out _));
    }

    [Fact]
    public async Task With_a_salt_a_caller_with_no_account_id_cannot_settle_and_nothing_is_stored()
    {
        Build(s => s.Salt = Salt);
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        var outcome = await Settle("r1", Person("alice"));

        Assert.Equal(IyuLowlineSettleStatus.Refused, outcome.Status);
        Assert.Empty(await _provider.GetRequiredService<ILowlineStore>().ReadRecordsAsync(new LowlineScope("order", "plant-a")));
    }

    [Fact]
    public async Task The_account_id_claim_can_be_another_claim()
    {
        Build(s =>
        {
            s.Salt = Salt;
            s.AccountClaimType = "employee_no";
        });
        _hook.Save("r1", "alice", "plant-a", "acme", "red");

        await Settle("r1", Person("alice", accountId: "ignored", new Claim("employee_no", "E-7")));

        Assert.Equal(Expected(Salt, "E-7"), (await Stored("r1")).GetProperty("settled_by").GetProperty("colour").GetString());
    }

    [Fact]
    public async Task A_service_client_cannot_settle_and_the_hook_is_not_asked()
    {
        Build(s => s.Salt = Salt);
        _hook.Save("r1", "alice", "plant-a", "acme", "red");
        var machine = Person("alice", accountId: "client-1", new Claim(IyuIdentityClaims.ServiceClientStampClaimType, "stamp"));

        var outcome = await Settle("r1", machine);

        Assert.Equal(IyuLowlineSettleStatus.Forbidden, outcome.Status);
        Assert.Equal(0, _hook.Reads);
        Assert.Empty(await _provider.GetRequiredService<ILowlineStore>().ReadRecordsAsync(new LowlineScope("order", "plant-a")));
    }

    [Fact]
    public void The_service_client_claim_this_package_looks_for_is_the_one_the_framework_issues()
    {
        // Iyu.Lowline does not reference Iyu.MainServer, so it carries the claim name as a literal.
        var machine = new ClaimsPrincipal(new ClaimsIdentity([new Claim(IyuIdentityClaims.ServiceClientStampClaimType, "x")], "test"));

        Assert.True(new IyuLowlineSettlerOptions().IsServiceAccount(machine));
        Assert.False(new IyuLowlineSettlerOptions().IsServiceAccount(Person("alice", "u")));
    }

    [Fact]
    public async Task A_salt_that_is_too_short_stops_the_host_without_saying_the_salt()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddDbContext<LowlineTestDb>(o => o.UseSqlite($"Data Source={_file}"));
        builder.Services.AddIyuLowline<LowlineTestDb, LowlineTestRecord, LowlineTestTuning>(o =>
        {
            o.Engine.Form("order", OrderForm);
            o.Settler.Salt = "too-short-secret";
        });
        await using var app = builder.Build();

        var refusal = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        var message = Assert.Single(refusal.Failures);
        Assert.Contains("Settler.Salt", message);
        Assert.DoesNotContain("too-short-secret", message);
    }
}
