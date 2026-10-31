using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Iyu.Uncad;

/// <summary>
/// Runs the <c>uncad</c> command-line program as a separate process and returns what it wrote to standard output.
/// </summary>
/// <remarks>
/// <para>
/// <b>License boundary.</b> <c>uncad</c> is a separate program under the GPL-3.0-or-later. This class only starts it as a
/// child process and reads its standard output: it does not link to it, load it or contain any of its code, and this package
/// does not ship the executable. The host installs the program and names it with <see cref="IyuUncadOptions.ExePath"/>.
/// </para>
/// <para>
/// <b>Limits.</b> A drawing is untrusted input, so a crash or a runaway in the program's parser is kept at the process
/// boundary. A run is limited in time (<see cref="IyuUncadOptions.Timeout"/>) and in the size of its output
/// (<see cref="IyuUncadOptions.MaxOutputBytes"/>); exceeding either ends the process and its descendants. Standard error is
/// read at the same time as standard output, since reading only one can fill the other's pipe and stop the program.
/// No shell is involved: arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so a file name is never read as a command
/// — provided <see cref="IyuUncadOptions.ExePath"/> is the program itself. On Windows a batch file is run through <c>cmd.exe</c>, which
/// parses its arguments again, so it must not be a <c>.cmd</c> or <c>.bat</c> wrapper.
/// </para>
/// <para>
/// <b>Known limitation:</b> there is no memory cap on the child process (an OS job object would be needed). A descendant that
/// has outlived its parent is likewise out of reach of the tree kill, because the tree is found through the parent; a job object
/// (or, on Linux, a process group) would close that too.
/// </para>
/// </remarks>
public sealed class UncadRunner(IOptions<IyuUncadOptions> options) : IUncadRunner
{
    private const int MaxStderrBytes = 16 * 1024;

    /// <inheritdoc />
    public async Task<UncadRunResult> RunAsync(
        string verb,
        string drawingPath,
        IReadOnlyList<string> extraArgs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(drawingPath);
        ArgumentNullException.ThrowIfNull(extraArgs);

        var settings = options.Value;
        var clock = Stopwatch.StartNew();

        // Checked per call, not at startup: the program can be installed after the host has started.
        if (ExistingFile(settings.ExePath) is not { } exe)
        {
            return UncadRunResult.Fail(
                UncadFailure.NotConfigured, "The uncad executable is not configured or does not exist at the configured path.", clock.Elapsed);
        }

        // Absolute, because the process runs in the temporary directory: a relative path that exists here would name another file there.
        // It also means an argument that begins with '-' cannot be read by the program as an option.
        if (!IsDrawing(drawingPath) || ExistingFile(drawingPath) is not { } drawing)
        {
            return UncadRunResult.Fail(
                UncadFailure.InvalidInput, "The drawing does not exist or is not a .dwg or .dxf file.", clock.Elapsed);
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(drawing);
        foreach (var arg in extraArgs) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return UncadRunResult.Fail(UncadFailure.StartFailed, "uncad could not be started.", clock.Elapsed);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return UncadRunResult.Fail(UncadFailure.StartFailed, "uncad could not be started: " + e.Message, clock.Elapsed);
        }

        process.StandardInput.Close();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(settings.Timeout);

        // Both streams are read at once. Output past the cap stops the read and the process is ended below; error output past its
        // own small cap is thrown away but still drained, or the program would block on a full pipe.
        var stdout = ReadCappedAsync(process.StandardOutput, settings.MaxOutputBytes, limit.Token);
        var stderr = ReadCappedAsync(process.StandardError, MaxStderrBytes, limit.Token, drainOnOverflow: true);
        try
        {
            var (outText, tooLarge) = await stdout;
            if (tooLarge)
            {
                Kill(process);
                return UncadRunResult.Fail(
                    UncadFailure.OutputTooLarge,
                    FormattableString.Invariant($"The output exceeded the limit of {settings.MaxOutputBytes} bytes."),
                    clock.Elapsed);
            }

            await process.WaitForExitAsync(limit.Token);
            var (errText, _) = await stderr;
            var warnings = Warnings(errText);
            if (process.ExitCode != 0)
            {
                return UncadRunResult.Fail(
                    UncadFailure.NonZeroExit,
                    string.Create(CultureInfo.InvariantCulture, $"uncad exited with code {process.ExitCode}."),
                    clock.Elapsed,
                    warnings);
            }

            return new UncadRunResult(true, outText, null, null, warnings, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            return UncadRunResult.Fail(
                UncadFailure.Timeout,
                string.Create(CultureInfo.InvariantCulture, $"uncad did not finish within {settings.Timeout.TotalSeconds:0.##} seconds."),
                clock.Elapsed);
        }
    }

    /// <summary>The full path of <paramref name="path"/> if a file is there; otherwise <c>null</c>.</summary>
    private static string? ExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return File.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static bool IsDrawing(string path)
        => path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase);

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Collects text up to <paramref name="maxBytes"/> bytes. Past the cap, a read that is not draining stops and reports it;
    /// one that is draining discards the rest and reads on to the end.
    /// </summary>
    private static async Task<(string Text, bool TooLarge)> ReadCappedAsync(
        StreamReader reader,
        long maxBytes,
        CancellationToken token,
        bool drainOnOverflow = false)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        long bytes = 0;
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            bytes += reader.CurrentEncoding.GetByteCount(buffer, 0, n);
            if (bytes > maxBytes)
            {
                if (!drainOnOverflow) return (sb.ToString(), true);
                continue; // Keep emptying the pipe, or the child blocks on its next write.
            }

            sb.Append(buffer, 0, n);
        }

        return (sb.ToString(), false);
    }

    private static IReadOnlyList<string> Warnings(string stderr)
        => stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
            .ToList();
}
