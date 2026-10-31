namespace Iyu.Tests.Uncad;

/// <summary>
/// A temporary directory of stand-in executables for the real <c>uncad</c> program, so the process boundary of
/// <c>UncadRunner</c> can be tested without the program: what it does with a child's output, exit code, run time and arguments.
/// </summary>
/// <remarks>
/// The stand-in is a different file on each platform — a <c>.cmd</c> batch file on Windows, a <c>#!/bin/sh</c> script marked
/// executable elsewhere — because the CI runner is Linux and the developer machines are often Windows. Every scenario is
/// therefore written twice, once per platform, and each test runs the variant its platform can execute; no test is skipped.
/// </remarks>
internal sealed class FakeUncadDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("uncad-fake-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A killed process can hold its script open for a moment longer. The directory is temporary; leave it.
        }
    }

    /// <summary>Where <see cref="Executable"/> puts the stand-in called <paramref name="name"/>.</summary>
    public string ExecutablePath(string name)
        => System.IO.Path.Combine(Path, OperatingSystem.IsWindows() ? name + ".cmd" : name);

    /// <summary>
    /// Writes the stand-in called <paramref name="name"/> — a batch file with the <paramref name="windows"/> lines, or a shell
    /// script with the <paramref name="unix"/> lines, whichever this platform runs — and returns its path.
    /// </summary>
    public string Executable(string name, string[] windows, string[] unix)
    {
        var path = ExecutablePath(name);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, "@echo off\r\n" + string.Join("\r\n", windows) + "\r\n");
            return path;
        }

        // Line endings are written out, not taken from a source file: a script whose first line ends in a carriage return has no
        // interpreter ("bad interpreter"), and a checkout that converts line endings would turn a literal block into one.
        File.WriteAllText(path, "#!/bin/sh\n" + string.Join("\n", unix) + "\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    /// <summary>A file the runner accepts as a drawing: it only has to exist and end in <c>.dwg</c> or <c>.dxf</c>.</summary>
    public string Drawing(string name = "sample.dwg")
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, "x");
        return path;
    }
}
