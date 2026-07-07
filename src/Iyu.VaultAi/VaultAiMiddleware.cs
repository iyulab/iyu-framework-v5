using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Iyu.VaultAi;

public static class VaultAiMiddleware
{
    public static IApplicationBuilder UseVaultAiReports(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices
            .GetRequiredService<IOptions<VaultAiSettings>>().Value;

        // "VaultAi" 섹션 미구성 시 미들웨어 자체를 등록하지 않는다
        // (AddVaultAiReports가 IVaultAiClient 등록을 생략한 상태).
        if (string.IsNullOrWhiteSpace(settings.Url))
            return app;

        var env = app.ApplicationServices
            .GetRequiredService<IWebHostEnvironment>();
        var client = app.ApplicationServices
            .GetRequiredService<IVaultAiClient>();

        // 사전 집계 데이터 주입기(소비앱이 등록). 미등록이면 {{data}}는 빈 안내로 치환된다.
        var dataProvider = app.ApplicationServices
            .GetService<IReportDataProvider>();

        var reportPath = Path.IsPathRooted(settings.ReportPath)
            ? settings.ReportPath
            : Path.Combine(env.ContentRootPath, settings.ReportPath);

        var basePath = "/" + settings.BasePath.Trim('/');

        // 리포트 파일 디렉터리 브라우징: {basePath}-files (예: /ai-reports-files)
        // reports 루트를 통째 노출 → output·logs·prompt.md 등을 브라우저에서 열람.
        // SPA prefix({basePath})와 분리된 별도 경로라 fallback 충돌 없음.
        Directory.CreateDirectory(reportPath);
        var filesRequestPath = basePath + "-files";
        var filesProvider    = new PhysicalFileProvider(reportPath);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider          = filesProvider,
            RequestPath           = filesRequestPath,
            ServeUnknownFileTypes = true,                       // .log 등 미등록 확장자 허용
            DefaultContentType    = "text/plain; charset=utf-8"
        });
        app.UseDirectoryBrowser(new DirectoryBrowserOptions
        {
            FileProvider = filesProvider,
            RequestPath  = filesRequestPath
        });

        var assembly = typeof(VaultAiMiddleware).Assembly;
        var fileProvider = new EmbeddedFileProvider(assembly, "Iyu.VaultAi.wwwroot");

        // index.html에 주입할 config 스크립트 (소비앱 설정 → SPA 런타임에 전달)
        var configJson = JsonSerializer.Serialize(new { basePath, title = settings.Title }, VaultAiReportsApi.WebJson);
        var configScript = $"<script>window.__VAULT_AI_CONFIG__={configJson}</script>";

        // 단일 Use() 미들웨어로 API + 정적파일 + SPA fallback 모두 처리.
        app.Use(async (context, next) =>
        {
            var path   = context.Request.Path.Value ?? "";
            var method = context.Request.Method;

            // ── API: /api/vault-ai/** ───────────────────────────────────────────
            if (path.StartsWith("/api/vault-ai/", StringComparison.OrdinalIgnoreCase))
            {
                // GET /api/vault-ai/reports → 목록
                if (method == "GET"
                    && path.Equals("/api/vault-ai/reports", StringComparison.OrdinalIgnoreCase))
                {
                    await context.Response.WriteAsJsonAsync(
                        VaultAiReportsApi.GetReports(reportPath), VaultAiReportsApi.WebJson);
                    return;
                }

                // /api/vault-ai/reports/{folder}[/**] 이하 모든 CRUD
                if (path.StartsWith("/api/vault-ai/reports/", StringComparison.OrdinalIgnoreCase))
                {
                    VaultAiReportsApi.TryParseSegments(path, out _, out var action, out var file);

                    // GET .../outputs/{file}
                    if (method == "GET"
                        && string.Equals(action, "outputs", StringComparison.OrdinalIgnoreCase)
                        && file is not null)
                    {
                        await VaultAiReportsApi.GetOutputAsync(context, reportPath);
                        return;
                    }

                    // POST .../generate
                    if (method == "POST"
                        && string.Equals(action, "generate", StringComparison.OrdinalIgnoreCase))
                    {
                        await VaultAiReportsApi.GenerateAsync(
                            context, reportPath, client, settings.ReportAgentId, settings, dataProvider);
                        return;
                    }

                    // PATCH .../{folder}  (action == null)
                    if (method == "PATCH" && action is null)
                    {
                        await VaultAiReportsApi.PatchInfoAsync(context, reportPath);
                        return;
                    }

                    // DELETE .../outputs/{file}
                    if (method == "DELETE"
                        && string.Equals(action, "outputs", StringComparison.OrdinalIgnoreCase)
                        && file is not null)
                    {
                        await VaultAiReportsApi.DeleteOutputAsync(context, reportPath);
                        return;
                    }

                    context.Response.StatusCode = 404;
                    return;
                }

                await next();
                return;
            }

            // ── 수동 생성 트리거: {basePath}/{folder}/new ──────────────────────
            // SPA fallback이 확장자 없는 경로를 index.html로 흡수하기 전에 가로챈다.
            if (method == "GET"
                && path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path[(basePath.Length + 1)..]
                    .Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length == 2
                    && string.Equals(segments[1], "new", StringComparison.OrdinalIgnoreCase))
                {
                    await VaultAiReportsApi.GenerateNewAsync(
                        context, reportPath, client, settings.ReportAgentId, settings, segments[0], dataProvider);
                    return;
                }
            }

            // ── Static + SPA: {basePath}/** (GET/HEAD 전용) ────────────────────
            // 정확히 basePath이거나 그 하위만 — "{basePath}-files" 같은 형제 경로는 제외.
            if ((method == "GET" || method == "HEAD")
                && (path.Equals(basePath, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)))
            {
                var subpath = path.Length > basePath.Length
                    ? path[basePath.Length..].TrimStart('/')
                    : "";

                // 확장자 있는 경로 → 내장 정적 파일 서빙
                if (!string.IsNullOrEmpty(subpath) && Path.HasExtension(subpath))
                {
                    var fileInfo = fileProvider.GetFileInfo(subpath);
                    if (fileInfo.Exists)
                    {
                        context.Response.ContentType = GetContentType(subpath);
                        await using var stream = fileInfo.CreateReadStream();
                        await stream.CopyToAsync(context.Response.Body);
                        return;
                    }
                }

                // SPA fallback → index.html + config 주입
                var index = fileProvider.GetFileInfo("index.html");
                if (index.Exists)
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await using var stream = index.CreateReadStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var html = await reader.ReadToEndAsync();

                    // BasePath가 기본값과 다른 경우: Vite 빌드 절대 경로 치환
                    if (!basePath.Equals("/vault-ai-reports", StringComparison.OrdinalIgnoreCase))
                        html = html.Replace("/vault-ai-reports/", basePath + "/",
                            StringComparison.OrdinalIgnoreCase);

                    // 소비앱 설정(basePath, title)을 SPA 전역 변수로 주입
                    html = html.Replace("</head>", configScript + "</head>",
                        StringComparison.OrdinalIgnoreCase);

                    var bytes = Encoding.UTF8.GetBytes(html);
                    await context.Response.Body.WriteAsync(bytes);
                }
                return;
            }

            await next();
        });

        return app;
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".js" or ".mjs" => "application/javascript",
            ".css"          => "text/css",
            ".html"         => "text/html; charset=utf-8",
            ".json"         => "application/json",
            ".svg"          => "image/svg+xml",
            ".png"          => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico"          => "image/x-icon",
            ".woff"         => "font/woff",
            ".woff2"        => "font/woff2",
            ".wasm"         => "application/wasm",   // declart-wasm 스트리밍 인스턴스화용
            _               => "application/octet-stream"
        };
    }
}
