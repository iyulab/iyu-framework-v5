using System.Diagnostics;
using Iyu.Uncad;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Uncad;

/// <summary>
/// The process boundary of <see cref="UncadRunner"/>: what it does with a child's output, warnings, exit code, run time and
/// arguments. The real program is not needed — a stand-in executable per platform plays it (see <see cref="FakeUncadDirectory"/>).
/// </summary>
public sealed class UncadRunnerTests : IDisposable
{
    private readonly FakeUncadDirectory _fake = new();

    public void Dispose() => _fake.Dispose();

    private static UncadRunner Runner(string? exe, int timeoutSeconds = 10, long maxBytes = 1024 * 1024)
        => new(Options.Create(new IyuUncadOptions
        {
            ExePath = exe,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            MaxOutputBytes = maxBytes,
        }));

    private string Echoing(string json)
        => _fake.Executable("echoing", windows: [$"echo {json}"], unix: [$"echo '{json}'"]);

    [Fact]
    public async Task A_successful_run_returns_stdout_and_the_stderr_warning_lines()
    {
        var exe = _fake.Executable(
            "ok",
            windows:
            [
                "echo {\"entity_count\":1}",
                "echo warning: 'x' was read with non-fatal problems (UNHANDLEDCLASS) 1>&2",
                "echo something else 1>&2",
            ],
            unix:
            [
                "echo '{\"entity_count\":1}'",
                "echo \"warning: 'x' was read with non-fatal problems (UNHANDLEDCLASS)\" >&2",
                "echo \"something else\" >&2",
            ]);

        var result = await Runner(exe).RunAsync("summarize", _fake.Drawing(), []);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("\"entity_count\":1", result.Json);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("warning:", warning);
    }

    [Fact]
    public async Task The_verb_the_path_and_extra_args_reach_the_process_in_order_without_a_shell()
    {
        // Spaces, parentheses and non-ASCII characters in the file name are what a shell would take apart or reinterpret. On Unix the
        // name also carries what a shell would expand or split on. A Windows batch file cannot echo such a name back (it expands its
        // parameters before it parses the line), so there the name stays with the first three.
        var exe = _fake.Executable(
            "args",
            windows: ["chcp 65001 >nul", "echo %~1^|%~2^|%~3^|%~4"],
            unix: ["printf '%s|%s|%s|%s\\n' \"$1\" \"$2\" \"$3\" \"$4\""]);
        var path = _fake.Drawing(
            OperatingSystem.IsWindows() ? "plan ü山 (1).dwg" : "plan ü山 (1); echo $HOME & more.dwg");

        var result = await Runner(exe).RunAsync("summarize", path, ["--type", "DIMENSION"]);

        Assert.True(result.Ok, result.Message);
        Assert.Equal($"summarize|{path}|--type|DIMENSION", result.Json!.Trim());
    }

    [Fact]
    public async Task A_relative_drawing_path_reaches_the_process_as_a_full_path()
    {
        // The process runs in the temporary directory, so a relative path that exists for the caller would name another file there.
        // The drawing sits under the current directory so that a relative path to it exists on every platform and drive.
        var under = Directory.CreateDirectory(
            Path.Combine(Environment.CurrentDirectory, "uncad-relative-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var drawing = Path.Combine(under, "rel.dwg");
            File.WriteAllText(drawing, "x");
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, drawing);
            Assert.False(Path.IsPathRooted(relative));

            var exe = _fake.Executable(
                "args",
                windows: ["echo %~1^|%~2^|%~3^|%~4"],
                unix: ["printf '%s|%s|%s|%s\\n' \"$1\" \"$2\" \"$3\" \"$4\""]);

            var result = await Runner(exe).RunAsync("summarize", relative, []);

            Assert.True(result.Ok, result.Message);
            Assert.Equal($"summarize|{drawing}||", result.Json!.Trim());
        }
        finally
        {
            Directory.Delete(under, recursive: true);
        }
    }

    [Fact]
    public async Task A_nonzero_exit_is_a_failure_that_keeps_the_warnings()
    {
        var exe = _fake.Executable(
            "fail",
            windows: ["echo warning: partial 1>&2", "exit /b 3"],
            unix: ["echo \"warning: partial\" >&2", "exit 3"]);

        var result = await Runner(exe).RunAsync("summarize", _fake.Drawing(), []);

        Assert.False(result.Ok);
        Assert.Equal(UncadFailure.NonZeroExit, result.Failure);
        Assert.Null(result.Json);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Exceeding_the_time_limit_is_a_timeout_and_ends_the_process()
    {
        // The stand-in would write a marker file after five seconds of waiting. A runner that only stopped listening, and left the
        // process running, would let the marker appear. A process that was ended can never write it, however late the limit fires.
        var marker = Path.Combine(_fake.Path, "survived.txt");
        var exe = _fake.Executable(
            "slow",
            windows: ["ping -n 6 127.0.0.1 >nul", $"echo survived> \"{marker}\""],
            unix: ["sleep 5", $"echo survived > '{marker}'"]);

        var clock = Stopwatch.StartNew();
        var result = await Runner(exe, timeoutSeconds: 1).RunAsync("summarize", _fake.Drawing(), []);
        var elapsed = clock.Elapsed;

        Assert.False(result.Ok);
        Assert.Equal(UncadFailure.Timeout, result.Failure);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"a limit of one second took {elapsed}");

        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(marker), "the process outlived the time limit");
    }

    [Fact]
    public async Task Output_over_the_cap_is_cut_and_reported()
    {
        var line = new string('x', 64);
        var exe = _fake.Executable(
            "big",
            windows: [$"for /L %%i in (1,1,400) do @echo {line}"],
            unix: ["i=0", $"while [ \"$i\" -lt 400 ]; do echo {line}; i=$((i+1)); done"]);

        var result = await Runner(exe, maxBytes: 1024).RunAsync("summarize", _fake.Drawing(), []);

        Assert.False(result.Ok);
        Assert.Equal(UncadFailure.OutputTooLarge, result.Failure);
    }

    [Fact]
    public async Task Exceeding_the_output_cap_ends_the_process_and_what_it_started()
    {
        // The stand-in starts a second process that would write a marker file after four seconds, and only then produces its
        // output, so the second process exists before the cap is hit and the order does not depend on how loaded the machine is.
        // It then stays alive: a process whose parent has already exited is out of the tree's reach (README, "Limits"), and a
        // stand-in that wrote its output and left at once would be exactly that by the time the cap is noticed.
        // If only the direct child were ended, the second process would outlive it and the marker would appear. The time limit and
        // the output cap end a run the same way, so this covers both.
        var marker = Path.Combine(_fake.Path, "survived.txt");
        var grandchild = _fake.Executable(
            "grandchild",
            windows: ["ping -n 5 127.0.0.1 >nul", $"echo survived> \"{marker}\""],
            unix: ["sleep 4", $"echo survived > '{marker}'"]);
        var line = new string('x', 64);
        var exe = _fake.Executable(
            "big",
            windows:
            [
                $"start \"\" /b \"{grandchild}\"",
                $"for /L %%i in (1,1,400) do @echo {line}",
                "ping -n 30 127.0.0.1 >nul",
            ],
            unix:
            [
                $"'{grandchild}' &",
                "i=0",
                $"while [ \"$i\" -lt 400 ]; do echo {line}; i=$((i+1)); done",
                "sleep 30",
            ]);

        var result = await Runner(exe, maxBytes: 1024).RunAsync("summarize", _fake.Drawing(), []);

        Assert.Equal(UncadFailure.OutputTooLarge, result.Failure);

        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(marker), "the process the stand-in started outlived the run");
    }

    [Fact]
    public async Task Standard_error_is_read_while_standard_output_is_read_so_a_chatty_program_does_not_block()
    {
        // More than a pipe holds (64 KiB on Linux, far less on Windows), written before any output. A runner that read standard
        // output first would wait for output the program cannot get to, until the time limit.
        var line = new string('y', 96);
        var exe = _fake.Executable(
            "chatty",
            windows: ["echo warning: first 1>&2", $"for /L %%i in (1,1,1500) do @echo {line} 1>&2", "echo {}"],
            unix:
            [
                "echo \"warning: first\" >&2",
                "i=0",
                $"while [ \"$i\" -lt 1500 ]; do echo {line} >&2; i=$((i+1)); done",
                "echo '{}'",
            ]);

        var result = await Runner(exe).RunAsync("summarize", _fake.Drawing(), []);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("{}", result.Json!.Trim());
        Assert.Equal("warning: first", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task A_missing_executable_or_no_path_is_not_configured()
    {
        Assert.Equal(UncadFailure.NotConfigured, (await Runner(null).RunAsync("summarize", _fake.Drawing(), [])).Failure);
        Assert.Equal(UncadFailure.NotConfigured, (await Runner("  ").RunAsync("summarize", _fake.Drawing(), [])).Failure);
        Assert.Equal(
            UncadFailure.NotConfigured,
            (await Runner(Path.Combine(_fake.Path, "absent")).RunAsync("summarize", _fake.Drawing(), [])).Failure);
    }

    [Fact]
    public async Task An_executable_installed_after_the_runner_was_made_is_found_on_the_next_call()
    {
        var runner = Runner(_fake.ExecutablePath("late"));
        var drawing = _fake.Drawing();

        Assert.Equal(UncadFailure.NotConfigured, (await runner.RunAsync("summarize", drawing, [])).Failure);

        _fake.Executable("late", windows: ["echo {}"], unix: ["echo '{}'"]);
        var result = await runner.RunAsync("summarize", drawing, []);

        Assert.True(result.Ok, result.Message);
    }

    [Theory]
    [InlineData("missing.dwg")]
    [InlineData("notes.txt")]
    public async Task Only_existing_dwg_or_dxf_files_are_accepted(string name)
    {
        var exe = Echoing("{}");
        var path = Path.Combine(_fake.Path, name);
        if (name.EndsWith(".txt", StringComparison.Ordinal)) File.WriteAllText(path, "x");

        var result = await Runner(exe).RunAsync("summarize", path, []);

        Assert.Equal(UncadFailure.InvalidInput, result.Failure);
    }

    [Fact]
    public async Task A_dxf_extension_is_accepted_whatever_its_case()
    {
        var result = await Runner(Echoing("{}")).RunAsync("summarize", _fake.Drawing("UPPER.DXF"), []);

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public async Task Cancelling_the_callers_token_throws_instead_of_reporting_a_timeout()
    {
        var exe = _fake.Executable(
            "slow",
            windows: ["ping -n 30 127.0.0.1 >nul"],
            unix: ["sleep 30"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Runner(exe).RunAsync("summarize", _fake.Drawing(), [], cts.Token));
    }
}
