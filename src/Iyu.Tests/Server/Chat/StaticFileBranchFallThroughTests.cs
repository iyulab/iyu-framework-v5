using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iyu.Tests.Server.Chat;

/// <summary>
/// iyu <c>UseDefaultsBegin</c>의 .js/.css 선처리 브랜치가 엔드포인트로 제공되는 동적 자산을
/// 가로채던 회귀를 고정한다. (BareChat의 /chat/app.js 등이 라이브에서 404 나던 근본 원인)
/// </summary>
/// <remarks>
/// 실제 <see cref="StaticFileExtensions.UseStaticFiles(IApplicationBuilder)"/> + MapWhen/UseWhen +
/// 다운스트림 엔드포인트로 메커니즘을 재현한다. 물리 파일이 없는 .js 요청에 대해:
/// MapWhen(terminal) → 404, UseWhen(재합류) → 엔드포인트가 처리 → 200.
/// </remarks>
public class StaticFileBranchFallThroughTests
{
    private static Func<HttpContext, bool> IsJsOrCss => ctx =>
    {
        var p = ctx.Request.Path.Value?.ToLowerInvariant();
        return p != null && (p.EndsWith(".js") || p.EndsWith(".css"));
    };

    private static async Task<HttpStatusCode> ProbeDynamicJsAsync(bool useWhen)
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "iyuchat-webroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webRoot); // 빈 webroot — /chat/app.js 는 물리 파일로 존재하지 않는다.

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        Action<IApplicationBuilder> staticBranch = b => b.UseStaticFiles();
        if (useWhen) app.UseWhen(IsJsOrCss, staticBranch);
        else app.MapWhen(IsJsOrCss, staticBranch);

        // 마운트형 서브앱이 동적으로 제공하는 .js 자산 핸들러 (BareChat /chat/app.js 모사).
        // 엔드포인트 대신 terminal 미들웨어로 두어 WebApplication 의 자동 UseRouting(파이프라인
        // 선두 주입)을 피하고, iyu 의 수동 순서(.js/.css 브랜치가 다운스트림보다 먼저)를 충실히 재현한다.
        app.Run(async ctx =>
        {
            if (ctx.Request.Path == "/chat/app.js")
            {
                ctx.Response.ContentType = "text/javascript";
                await ctx.Response.WriteAsync("console.log('ok')");
            }
            else
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        await app.StartAsync();
        try
        {
            using var resp = await app.GetTestClient().GetAsync("/chat/app.js");
            return resp.StatusCode;
        }
        finally
        {
            await app.DisposeAsync();
            try { Directory.Delete(webRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task UseWhen_RejoinsPipeline_DynamicJsAssetServedByEndpoint()
    {
        Assert.Equal(HttpStatusCode.OK, await ProbeDynamicJsAsync(useWhen: true));
    }

    [Fact]
    public async Task MapWhen_IsTerminal_DynamicJsAsset404s()
    {
        // 회귀의 근본 원인을 문서화: terminal 브랜치는 동적 .js 자산을 가로채 404 처리한다.
        Assert.Equal(HttpStatusCode.NotFound, await ProbeDynamicJsAsync(useWhen: false));
    }
}
