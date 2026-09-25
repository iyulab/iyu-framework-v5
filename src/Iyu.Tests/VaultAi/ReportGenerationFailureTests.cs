using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Iyu.VaultAi;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// What a failed report generation tells its caller. Only the failures this module raises on
/// purpose carry their own text and a stable code; any other exception's message stays in the
/// logs — it can name server paths or carry an upstream service's response body, and it tells
/// the caller nothing it can act on.
/// </summary>
public sealed class ReportGenerationFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iyu-genfail-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private sealed class UnusedClient : IVaultAiClient
    {
        public Task<string> GetMessageAsync(Guid agentId, string prompt, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<JsonNode> GetStructuredMessageAsync(Guid agentId, string prompt, JsonNode outputSchema,
            IReadOnlyList<VaultAiImage>? images = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public void An_unexpected_exception_is_reported_without_its_message()
    {
        // The shape an I/O race produces: the runtime's own text, with a full server path.
        var failure = VaultAiReportsApi.DescribeFailure(
            new FileNotFoundException(@"Could not find file 'C:\srv\vault\reports\daily\prompt.md'."));

        Assert.Equal(500, failure.Status);
        Assert.Equal(VaultAiReportsApi.UnexpectedFailureCode, failure.Code);
        Assert.DoesNotContain(@"C:\srv", failure.Message);
        Assert.DoesNotContain("Could not find file", failure.Message);
    }

    [Fact]
    public void An_upstream_error_body_does_not_reach_the_caller()
    {
        var failure = VaultAiReportsApi.DescribeFailure(
            new InvalidOperationException("upstream said: {\"trace\":\"internal-host:8080 stack...\"}"));

        Assert.Equal(VaultAiReportsApi.UnexpectedFailureCode, failure.Code);
        Assert.DoesNotContain("internal-host", failure.Message);
    }

    [Fact]
    public void A_failure_this_module_raises_keeps_its_text_code_and_status()
    {
        var failure = VaultAiReportsApi.DescribeFailure(
            new ReportGenerationBusyException("리포트가 이미 생성 중입니다: daily"));

        Assert.Equal((409, "report_busy", "리포트가 이미 생성 중입니다: daily"), failure);
    }

    [Fact]
    public async Task A_report_folder_without_a_prompt_answers_404_with_a_code_and_no_path()
    {
        var folder = Path.Combine(_root, "daily");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "info.json"), """{"name":"daily"}""");

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/vault-ai/reports/daily/generate";
        ctx.Response.Body = new MemoryStream();

        await VaultAiReportsApi.GenerateAsync(ctx, _root, new UnusedClient(), Guid.NewGuid(), new VaultAiSettings());

        Assert.Equal(404, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal("prompt_missing", body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(_root, body.RootElement.GetProperty("error").GetString()!);
    }
}
