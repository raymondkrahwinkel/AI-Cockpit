using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cockpit.Infrastructure;

internal static class CommandRunnerProcess
{
    // Windows cmd.exe interprets forward slashes in argv[0] as switches, even when CreateProcess found it.
    internal static string NormalizeExecutablePath(string command) => command.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    // A refused kill can keep a pipe open, so abandon a read after a short grace instead of waiting forever.
    private static readonly TimeSpan ReadGrace = TimeSpan.FromSeconds(5);

    private const string Setsid = "/usr/bin/setsid";
    private const int SigKill = 9;

    // AC-2333 S5: starts a command whose whole tree ends when the result is disposed, orphans included. Windows starts it
    // suspended inside a kill-on-close job; Linux gives it its own process group. Either containment failing starts
    // nothing. Elsewhere (macOS) it is the tree walk, which only reaches descendants of a parent still alive.
    internal static ContainedRun StartContained(string executable, IReadOnlyList<string> arguments, string directory, IReadOnlyDictionary<string, string> environment)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsContainedStart.Start(NormalizeExecutablePath(executable), arguments, directory, environment);
        }

        var ownGroup = OperatingSystem.IsLinux();
        if (ownGroup && !File.Exists(Setsid))
        {
            throw new InvalidOperationException($"{Setsid} is missing, so the command's processes could not be contained.");
        }

        var startInfo = new ProcessStartInfo(ownGroup ? Setsid : executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Not a group leader, so setsid execs in place: the process keeps its pid, which is now its group id too.
        foreach (var argument in ownGroup ? arguments.Prepend(executable) : arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (variable, value) in environment)
        {
            startInfo.Environment[variable] = value;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The command did not start.");
        var id = process.Id;
        return new ContainedRun(
            process.StandardOutput,
            process.StandardError,
            process.WaitForExitAsync,
            () => process.HasExited ? process.ExitCode : null,
            () =>
            {
                if (ownGroup)
                {
                    _ = kill(-id, SigKill);
                }
                else
                {
                    _KillTree(process);
                }

                process.Dispose();
            });
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    internal static void _KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                if (OperatingSystem.IsLinux())
                {
                    _KillLinuxDescendants(process.Id);
                }

                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    // Process.Kill(entireProcessTree: true) does not reach all descendants on Linux. Read procfs before
    // terminating the root so shells waiting for a background child do not leave it running.
    private static void _KillLinuxDescendants(int parentId)
    {
        foreach (var childId in _ChildProcessIds(parentId))
        {
            try
            {
                using var child = Process.GetProcessById(childId);
                if (!child.HasExited)
                {
                    _KillLinuxDescendants(childId);
                    child.Kill();
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // The child raced this scan to exit, or its PID has already disappeared.
            }
        }
    }

    private static IEnumerable<int> _ChildProcessIds(int parentId)
    {
        string[] childFiles;
        try
        {
            childFiles = Directory.GetDirectories($"/proc/{parentId}/task")
                .Select(task => Path.Combine(task, "children"))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var childIds = new HashSet<int>();
        foreach (var childFile in childFiles)
        {
            string children;
            try
            {
                children = File.ReadAllText(childFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(child, out var childId))
                {
                    childIds.Add(childId);
                }
            }
        }

        foreach (var childId in childIds)
        {
            yield return childId;
        }
    }

    internal static async Task<string> _DrainAsync(Task<string> read)
    {
        try
        {
            return await read.WaitAsync(ReadGrace).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            return string.Empty;
        }
    }
}
