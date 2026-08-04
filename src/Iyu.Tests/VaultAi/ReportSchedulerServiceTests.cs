using System.Text.Json.Nodes;
using Iyu.VaultAi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// The scheduler's contract with an operator: a report that fails to generate leaves a visible
/// marker instead of a hole, and a run of failures escalates to <c>Critical</c>.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of the marker is that a missed report must not be <i>silent</i>. That makes the
/// marker and the escalation threshold the two things worth pinning here — if either stops working,
/// the failure it exists to surface goes back to being invisible, and nothing else in the suite
/// would notice.
/// </para>
/// <para>
/// One scheduler pass is awaited per case. The AI client is made to throw, which is the failure
/// mode being tested — no schedule is faked and no timing is relied on.
/// </para>
/// </remarks>
public sealed class ReportSchedulerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iyu-sched-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    /// <summary>An AI client that fails every call — the condition the marker exists for.</summary>
    private sealed class FailingClient : IVaultAiClient
    {
        public Task<string> GetMessageAsync(Guid agentId, string prompt, CancellationToken ct = default)
            => throw new InvalidOperationException("vault-ai unreachable");

        public Task<JsonNode> GetStructuredMessageAsync(Guid agentId, string prompt, JsonNode outputSchema,
            IReadOnlyList<VaultAiImage>? images = null, CancellationToken ct = default)
            => throw new InvalidOperationException("vault-ai unreachable");
    }

    private sealed class CapturingLogger : ILogger<ReportSchedulerService>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
    }

    /// <summary>A report folder due every minute, so the immediate first pass always has work.</summary>
    private string DefineReport(string name, bool enabled = true, string cron = "* * * * *")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "info.json"),
            $$"""{"name":"{{name}}","cron":"{{cron}}","enabled":{{(enabled ? "true" : "false")}}}""");
        return folder;
    }

    /// <remarks>
    /// The tick body is awaited directly rather than driven through <c>StartAsync</c>: how far the
    /// loop runs before the host's start call returns is not a property this service promises, and
    /// depending on it makes every assertion below a race.
    /// </remarks>
    private async Task<CapturingLogger> RunOnePassAsync()
    {
        var logger = new CapturingLogger();
        var settings = Options.Create(new VaultAiSettings { ReportPath = _root, ReportAgentId = Guid.NewGuid() });
        var service = new ReportSchedulerService(logger, new FailingClient(), settings, new StubEnvironment());

        await service.RunDueReportsAsync();
        return logger;
    }

    private static IReadOnlyList<string> Outputs(string folder)
    {
        var dir = Path.Combine(folder, "output");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.md").Order(StringComparer.Ordinal).ToList() : [];
    }

    [Fact]
    public async Task A_failed_report_leaves_a_marker_rather_than_a_hole()
    {
        var folder = DefineReport("daily");

        await RunOnePassAsync();

        var written = Assert.Single(Outputs(folder));
        var body = await File.ReadAllTextAsync(written);

        Assert.Contains("리포트 자동 생성 실패", body, StringComparison.Ordinal);
        Assert.Contains("daily", body, StringComparison.Ordinal);
        // The marker is what the next run reads to skip this slot when loading prior context.
        Assert.Contains("vault-ai:failure", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// One failure is a bad minute; a run of them is an outage. The escalation is the only thing
    /// that distinguishes the two for an operator who is not watching the folder.
    /// </summary>
    [Fact]
    public async Task A_single_failure_does_not_escalate_but_a_run_of_them_does()
    {
        var folder = DefineReport("daily");
        var output = Path.Combine(folder, "output");
        Directory.CreateDirectory(output);

        var first = await RunOnePassAsync();
        Assert.DoesNotContain(first.Entries, e => e.Level == LogLevel.Critical);

        // A second failure, in an earlier slot than the one just written, so the newest-first scan
        // walks a run of two. (The live scheduler reaches this state across two ticks.)
        var existing = Assert.Single(Outputs(folder));
        File.Copy(existing, Path.Combine(output, "19990101-0000.md"));
        File.Delete(existing);

        var second = await RunOnePassAsync();

        var critical = Assert.Single(second.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("연속", critical.Message, StringComparison.Ordinal);
        Assert.Contains("daily", critical.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A successful report between failures resets the run — the escalation must describe the
    /// current outage, not every failure the folder has ever held.
    /// </summary>
    [Fact]
    public async Task A_successful_report_between_failures_resets_the_run()
    {
        var folder = DefineReport("daily");
        var output = Path.Combine(folder, "output");
        Directory.CreateDirectory(output);

        // Oldest to newest: failure, then a real report. The scan stops at the real one.
        await File.WriteAllTextAsync(Path.Combine(output, "19990101-0000.md"), "old failure <!-- vault-ai:failure -->");
        await File.WriteAllTextAsync(Path.Combine(output, "19990102-0000.md"), "# a real report");

        var logger = await RunOnePassAsync();

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task A_disabled_report_is_not_run()
    {
        var folder = DefineReport("paused", enabled: false);

        await RunOnePassAsync();

        Assert.Empty(Outputs(folder));
    }

    /// <summary>
    /// A folder whose definition cannot be read is logged and skipped — it must not stop the
    /// reports after it, which are unrelated.
    /// </summary>
    [Fact]
    public async Task A_malformed_definition_is_skipped_without_stopping_the_others()
    {
        var broken = Path.Combine(_root, "aaa-broken");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, "info.json"), "{ not json");

        var healthy = DefineReport("zzz-healthy");   // sorts after the broken one

        var logger = await RunOnePassAsync();

        Assert.Empty(Outputs(broken));
        Assert.Single(Outputs(healthy));             // the later folder still ran
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>A missing report root is an unconfigured deployment, not a crash.</summary>
    [Fact]
    public async Task A_missing_report_root_is_tolerated()
    {
        var logger = await RunOnePassAsync();   // _root was never created

        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }
}
