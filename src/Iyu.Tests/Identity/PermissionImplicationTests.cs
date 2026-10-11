using System.Net;
using System.Net.Http.Headers;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// 권한 «함의» — 넓은 코드를 가진 쪽이 좁은 코드의 정책도 충족한다. 프레임워크가 권한 코드로 판정하는 두 자리
/// (카탈로그 코드마다의 정책 · 서비스 클라이언트가 소유자를 넘지 않는다는 규칙)가 같은 선언을 따라야 한다 —
/// 한쪽만 따르면 «정책은 통과하는데 그 권한을 위임할 수 없다»가 된다.
/// </summary>
public class PermissionImplicationTests
{
    private const string Read = "orders.read";
    private const string Write = "orders.write";
    private const string Admin = "orders.admin";
    private const string Other = "payments.read";

    private static readonly string[] Catalog = [Read, Write, Admin, Other];

    private static PermissionHierarchy Chain() =>
        new(Catalog, [new(Admin, Write), new(Write, Read)]);

    // ── 계층 자체 ──────────────────────────────────────────────

    [Fact]
    public void Implication_is_transitive_in_both_directions()
    {
        var h = Chain();
        Assert.Equal(new[] { Admin, Read, Write }, h.SatisfiedBy(Read));
        Assert.Equal(new[] { Admin, Write }, h.SatisfiedBy(Write));
        Assert.Equal(new[] { Admin }, h.SatisfiedBy(Admin));
        Assert.Equal(new[] { Other }, h.SatisfiedBy(Other));

        Assert.Equal(new[] { Admin, Read, Write }, h.Expand([Admin]));
        Assert.Equal(new[] { Read, Other }, h.Expand([Read, Other, Read]));
    }

    [Fact]
    public void Flat_implies_nothing()
    {
        Assert.Equal(new[] { Read }, PermissionHierarchy.Flat.SatisfiedBy(Read));
        Assert.Equal(new[] { Read, Write }, PermissionHierarchy.Flat.Expand([Write, Read]));
    }

    [Theory]
    [InlineData("orders.raed", Read)]   // 오타 — 조용히 아무것도 함의하지 않게 두지 않는다
    [InlineData(Write, "orders.raed")]
    public void A_code_outside_the_catalog_is_refused(string code, string implied)
    {
        var ex = Assert.Throws<ArgumentException>(() => new PermissionHierarchy(Catalog, [new(code, implied)]));
        Assert.Contains("orders.raed", ex.Message);
    }

    [Fact]
    public void A_code_implying_itself_is_refused() =>
        Assert.Throws<ArgumentException>(() => new PermissionHierarchy(Catalog, [new(Read, Read)]));

    [Fact]
    public void Codes_implying_each_other_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new PermissionHierarchy(Catalog, [new(Read, Write), new(Write, Read)]));
        // 다른 코드를 거쳐 돌아오는 것도
        var ex = Assert.Throws<ArgumentException>(() =>
            new PermissionHierarchy(Catalog, [new(Admin, Write), new(Write, Read), new(Read, Admin)]));
        Assert.Contains("imply each other", ex.Message);
    }

    [Fact]
    public void AddIyuIdentity_refuses_an_implication_outside_its_catalog()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddIyuIdentity(
            new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef" },
            permissionCatalog: [Read],
            permissionImplications: [new(Write, Read)]));
    }

    // ── 실제 파이프라인 — Bearer 검증 · 정책 · 위임 ────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public required FakeIdentityStore Store { get; init; }

        public Guid Owner(params string[] perms) => Store.AddUser($"owner-{Guid.NewGuid():N}", "소유자", perms: perms);

        /// <summary>저장소에 직접 넣은 클라이언트(소유자 검사를 거치지 않는다)의 토큰.</summary>
        public async Task<TokenResult> TokenAsync(Guid owner, string[] perms, string? scope = null)
        {
            var (clientId, secret, hash) = ServiceClientSecrets.Generate();
            Store.AddClient(clientId, hash, owner, perms: perms);
            return await IssueAsync(clientId, secret, scope);
        }

        public async Task<TokenResult> IssueAsync(string clientId, string secret, string? scope = null)
        {
            using var s = App.Services.CreateScope();
            return await s.ServiceProvider.GetRequiredService<IdentityTokenService>()
                .IssueClientCredentialsAsync(clientId, secret, scope, default);
        }

        public async Task<HttpStatusCode> GetAsync(string path, TokenResult token)
        {
            Assert.True(token.Ok, token.Error);
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var res = await Http.SendAsync(req);
            return res.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(IEnumerable<PermissionImplication>? implications)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var store = new FakeIdentityStore();
        builder.Services.AddSingleton<IIdentityStore>(store);
        builder.Services.AddSingleton<IServiceClientStore>(store);
        builder.Services.AddSingleton<IUserClaimsSource>(new FakeUserClaimsSource());
        builder.Services.AddIyuIdentity(
            // 매 요청 저장소 대조 — 위임된 권한의 지문이 검증 단계에서도 같게 계산되는지가 여기서 드러난다.
            new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef", ServiceClientValidationWindow = TimeSpan.Zero },
            permissionCatalog: Catalog,
            permissionImplications: implications);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/read", () => "ok").RequireAuthorization(Read);
        app.MapGet("/write", () => "ok").RequireAuthorization(Write);
        await app.StartAsync();
        return new Harness { App = app, Http = app.GetTestClient(), Store = store };
    }

    private static readonly PermissionImplication[] ChainImplications = [new(Admin, Write), new(Write, Read)];

    [Fact]
    public async Task A_broader_code_satisfies_the_narrower_policy_and_not_the_reverse()
    {
        await using var h = await StartAsync(ChainImplications);
        var owner = h.Owner(Admin);

        var write = await h.TokenAsync(owner, [Write]);
        Assert.Equal(HttpStatusCode.OK, await h.GetAsync("/read", write));
        Assert.Equal(HttpStatusCode.OK, await h.GetAsync("/write", write));

        var admin = await h.TokenAsync(owner, [Admin]);   // 두 단계 건너
        Assert.Equal(HttpStatusCode.OK, await h.GetAsync("/read", admin));

        var read = await h.TokenAsync(owner, [Read]);
        Assert.Equal(HttpStatusCode.Forbidden, await h.GetAsync("/write", read));
    }

    [Fact]
    public async Task Without_implications_a_broader_code_is_just_another_code()
    {
        await using var h = await StartAsync(implications: null);
        var write = await h.TokenAsync(h.Owner(Write), [Write]);
        Assert.Equal(HttpStatusCode.Forbidden, await h.GetAsync("/read", write));
    }

    [Fact]
    public async Task An_owner_may_delegate_a_code_they_hold_through_implication()
    {
        await using var h = await StartAsync(ChainImplications);
        var owner = h.Owner(Write);

        using var s = h.App.Services.CreateScope();
        var clients = s.ServiceProvider.GetRequiredService<ServiceClientService>();
        var created = await clients.CreateAsync(owner, "reader", [Read], expiresAt: null, default);
        Assert.True(created.Ok, created.Error);

        var token = await h.IssueAsync(created.ClientId!, created.PlaintextSecret!);
        Assert.Equal(new[] { Read }, token.Permissions);   // 발급된 것만 — 함의를 펼쳐 싣지 않는다
        Assert.Equal(HttpStatusCode.OK, await h.GetAsync("/read", token));
        Assert.Equal(HttpStatusCode.Forbidden, await h.GetAsync("/write", token));

        // 넓은 쪽은 여전히 넘는다
        var exceeding = await clients.CreateAsync(owner, "admin", [Admin], expiresAt: null, default);
        Assert.Equal(new[] { Admin }, exceeding.Exceeding);
    }

    [Fact]
    public async Task A_token_may_be_narrowed_to_a_code_the_client_holds_through_implication()
    {
        await using var h = await StartAsync(ChainImplications);

        var narrowed = await h.TokenAsync(h.Owner(Admin), [Write], scope: Read);
        Assert.Equal(new[] { Read }, narrowed.Permissions);
        Assert.Equal(HttpStatusCode.OK, await h.GetAsync("/read", narrowed));
        Assert.Equal(HttpStatusCode.Forbidden, await h.GetAsync("/write", narrowed));

        var beyond = await h.TokenAsync(h.Owner(Admin), [Write], scope: Admin);
        Assert.Equal("invalid_scope", beyond.Error);
    }
}
