using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Iyu.VaultAI.FolderChatbots;

/// <summary>
/// Where an open file really is, asked of the operating system about the open handle instead of read off the path that was used to open
/// it. A path can be made to lead somewhere else for the instant a file is opened (a folder swapped for a link and back); the handle
/// cannot - it is bound to the file it opened. Windows answers with the final path name of the handle, Linux with the link of the
/// descriptor under <c>/proc/self/fd</c>; elsewhere there is no answer and the callers keep the path checks they already make.
/// </summary>
internal static class FolderChatbotRealPath
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Throws if the file held by <paramref name="stream"/> is not inside <paramref name="directory"/> (itself followed through any link,
    /// since the administrator may have put the bot's folder behind one). Does nothing where the operating system cannot say.
    /// </summary>
    public static void ThrowIfOutside(FileStream stream, string directory, string what)
    {
        var actual = OfOpenFile(stream.SafeFileHandle);
        if (actual is null) return;
        var real = OfDirectory(directory);
        if (real is null) return;
        var inside = real.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!actual.StartsWith(inside, PathComparison))
            throw new IOException($"{what} was opened outside its folder (through a link), which is not followed.");
    }

    /// <summary>The path of the file an open handle holds, or null where the operating system cannot say.</summary>
    public static string? OfOpenFile(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows()) return WindowsFinalPath(handle);
        if (OperatingSystem.IsLinux())
        {
            var target = new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}").LinkTarget;
            return target is { Length: > 0 } && Path.IsPathRooted(target) ? target : null;
        }
        return null;
    }

    /// <summary>The folder with every link on the way to it followed, or null where the operating system cannot say.</summary>
    public static string? OfDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows())
        {
            // Query-only handle on the folder (no access wanted, every sharing allowed, backup semantics so a folder can be opened).
            using var handle = CreateFileW(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            return handle.IsInvalid ? null : WindowsFinalPath(handle);
        }
        return OperatingSystem.IsLinux() ? Resolve(full) : null;
    }

    /// <summary>Follows each component of an absolute path that is a link (a link that points at a link included), the way the system would.</summary>
    internal static string Resolve(string absolute)
    {
        var current = Path.GetPathRoot(absolute) ?? "/";
        var pending = new Stack<string>(Split(absolute[current.Length..]).Reverse());
        var hops = 0;
        while (pending.Count > 0)
        {
            var part = pending.Pop();
            if (part == ".") continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }
            var next = Path.Combine(current, part);
            var target = new DirectoryInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }
            if (++hops > 40) throw new IOException($"Too many links followed while resolving '{absolute}'.");
            var resolved = Path.GetFullPath(target, current);
            current = Path.GetPathRoot(resolved) ?? "/";
            foreach (var step in Split(resolved[current.Length..]).Reverse()) pending.Push(step);
        }
        return current;
    }

    private static IEnumerable<string> Split(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static string? WindowsFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) return null;
            if (length < buffer.Length)
            {
                var path = new string(buffer, 0, (int)length);
                if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
                return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
            }
            buffer = new char[length + 1];   // too small: the call returns the size it needs, terminator included
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
