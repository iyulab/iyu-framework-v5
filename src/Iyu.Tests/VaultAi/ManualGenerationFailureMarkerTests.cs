using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iyu.VaultAi;
using Xunit;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// A report generation started by hand (<c>GET …/{folder}/new</c>, answered 202 before it runs)
/// that fails leaves the same visible marker a failed scheduled run does — the caller otherwise has
/// to discover on their own that nothing arrived.
/// </summary>
public sealed class ManualGenerationFailureMarkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iyu-manual-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private sealed class FailingClient : IVaultAiClient
    {
        public Task<string> GetMessageAsync(Guid agentId, string prompt, CancellationToken ct = default)
            => throw new InvalidOperationException("vault-ai unreachable");

        public Task<JsonNode> GetStructuredMessageAsync(Guid agentId, string prompt, JsonNode outputSchema,
            IReadOnlyList<VaultAiImage>? images = null, CancellationToken ct = default)
            => throw new InvalidOperationException("vault-ai unreachable");
    }

    private string DefineReport(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "info.json"), $$"""{"name":"{{name}}","cron":"0 9 * * *","enabled":true}""");
        // No prompt.md: the run fails at once. The marker does not depend on why it failed, and an AI
        // failure would cost the retry backoff on every case.
        return folder;
    }

    private static IReadOnlyList<string> Outputs(string folder)
    {
        var dir = Path.Combine(folder, "output");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.md").Order(StringComparer.Ordinal).ToList() : [];
    }

    private static Task RunAsync(string folder) => VaultAiReportsApi.RunGenerateInBackgroundAsync(
        folder, new FailingClient(), Guid.NewGuid(), new VaultAiSettings { ReportPath = Path.GetDirectoryName(folder)! },
        dataProvider: null, logger: null);

    [Fact]
    public async Task A_failed_manual_generation_leaves_a_marker()
    {
        var folder = DefineReport("daily");

        await RunAsync(folder);

        var marker = Assert.Single(Outputs(folder));
        var body = await File.ReadAllTextAsync(marker);
        Assert.Contains("리포트 수동 생성 실패", body, StringComparison.Ordinal);
        Assert.Contains("daily", body, StringComparison.Ordinal);
        Assert.Contains(VaultAiReportsApi.FailureMarker, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scheduled slot is named <c>yyyyMMdd-HHmm.md</c> and skipped when that file exists. A manual
    /// marker under the same name would make the scheduler treat that minute's report as done.
    /// </summary>
    [Fact]
    public async Task The_manual_marker_does_not_take_a_scheduled_slot_name()
    {
        var folder = DefineReport("daily");

        await RunAsync(folder);

        var name = Path.GetFileName(Assert.Single(Outputs(folder)));
        Assert.DoesNotMatch(new Regex(@"^\d{8}-\d{4}\.md$"), name);
        Assert.EndsWith("-manual-failed.md", name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consecutive_manual_failures_count_toward_the_same_run()
    {
        var folder = DefineReport("daily");
        await RunAsync(folder);
        // The next attempt lands in a later minute's file; simulate it by renaming the first one back.
        var first = Assert.Single(Outputs(folder));
        File.Move(first, Path.Combine(Path.GetDirectoryName(first)!, "20000101-0000-manual-failed.md"));

        await RunAsync(folder);

        var latest = Outputs(folder).Last();
        Assert.Contains("| 연속 실패 | 2회 |", await File.ReadAllTextAsync(latest), StringComparison.Ordinal);
    }
}
