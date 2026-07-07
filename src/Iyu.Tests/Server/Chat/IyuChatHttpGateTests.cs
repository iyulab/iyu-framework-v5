using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Iyu.Server.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Server.Chat;

/// <summary>
/// 어댑터 HTTP 파이프라인 게이트 검증: <c>UseIyuChat</c>이 <c>Chat:Enabled</c>에 따라
/// 실제로 라우트를 마운트/생략하는지를 인메모리 TestServer로 확인한다.
/// (DI 등록은 <see cref="IyuChatGateTests"/>가 담당 — 여기서는 미들웨어 마운트가 대상.)
/// </summary>
public class IyuChatHttpGateTests
{
    private static Task<HttpStatusCode> ProbeHealthzAsync(bool enabled) => ProbePathAsync(enabled, "/chat/healthz");

    private static async Task<HttpStatusCode> ProbePathAsync(bool enabled, string path)
    {
        // BareChat은 시작 시 {DataPath}/chat.db 를 생성하므로 테스트 임시 경로로 격리한다.
        var dataPath = Path.Combine(Path.GetTempPath(), "iyuchat-test-" + Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:Enabled"] = enabled ? "true" : "false",
            ["Chat:DataPath"] = dataPath,
        });
        builder.Services.AddIyuChat(builder.Configuration);

        var app = builder.Build();
        app.UseIyuChat();
        await app.StartAsync();
        try
        {
            var client = app.GetTestClient();
            using var resp = await client.GetAsync(path);
            return resp.StatusCode;
        }
        finally
        {
            await app.DisposeAsync();
            try { Directory.Delete(dataPath, recursive: true); } catch { /* best-effort 정리 */ }
        }
    }

    [Fact]
    public async Task UseIyuChat_WhenEnabled_MountsHealthz()
    {
        Assert.Equal(HttpStatusCode.OK, await ProbeHealthzAsync(enabled: true));
    }

    [Fact]
    public async Task UseIyuChat_WhenDisabled_DoesNotMountHealthz()
    {
        Assert.Equal(HttpStatusCode.NotFound, await ProbeHealthzAsync(enabled: false));
    }

    [Theory]
    [InlineData("/chat/app.js")]
    [InlineData("/chat/app.css")]
    [InlineData("/chat/signalr.min.js")]
    [InlineData("/chat/sw.js")]
    public async Task UseIyuChat_WhenEnabled_ServesEmbeddedUiAssets(string path)
    {
        Assert.Equal(HttpStatusCode.OK, await ProbePathAsync(enabled: true, path));
    }
}
