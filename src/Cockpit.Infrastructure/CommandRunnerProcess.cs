using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure;

internal static class CommandRunnerProcess
{
    // Windows cmd.exe interprets forward slashes in argv[0] as switches, even when CreateProcess found it.
    internal static string NormalizeExecutablePath(string command) => command.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    // A refused kill can keep a pipe open, so abandon a read after a short grace instead of waiting forever.
    private static readonly TimeSpan ReadGrace = TimeSpan.FromSeconds(5);

    private const string Setsid = "/usr/bin/setsid";
    private const int SigKill = 9;

    // AC-2333 S5: starts `process` so that everything it starts can be ended with it, also children that outlived it.
    // Windows puts it in a kill-on-close job, Linux in its own process group via setsid; disposing the result ends
    // whatever is left. Without either, it falls back to the tree walk, which only reaches a parent still alive.
    // ponytail: a child spawned in the first instant on Windows, before the job assignment, escapes it; start
    // suspended if a command ever does that.
    internal static IDisposable StartContained(Process process)
    {
        var info = process.StartInfo;
        var ownGroup = OperatingSystem.IsLinux() && File.Exists(Setsid);
        if (ownGroup)
        {
            // Not a group leader, so setsid execs in place: the process keeps its pid, which is now its group id too.
            info.ArgumentList.Insert(0, info.FileName);
            info.FileName = Setsid;
        }

        process.Start();
        if (OperatingSystem.IsWindows() && _Job(process) is { } job)
        {
            return job;
        }

        var id = process.Id;
        return new Ending(() =>
        {
            if (ownGroup)
            {
                _ = kill(-id, SigKill);
            }
            else
            {
                _KillTree(process);
            }
        });
    }

    [SupportedOSPlatform("windows")]
    private static Ending? _Job(Process process)
    {
        var job = WindowsJobSessionAnchor.NativeMethods.CreateJobObjectW(IntPtr.Zero, null!);
        var limits = new WindowsJobSessionAnchor.NativeMethods.ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = WindowsJobSessionAnchor.NativeMethods.JobObjectLimitKillOnJobClose;
        if (job != IntPtr.Zero
            && WindowsJobSessionAnchor.NativeMethods.SetInformationJobObject(job, WindowsJobSessionAnchor.NativeMethods.JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf(limits))
            && WindowsJobSessionAnchor.NativeMethods.AssignProcessToJobObject(job, process.Handle))
        {
            return new Ending(() =>
            {
                WindowsJobSessionAnchor.NativeMethods.TerminateJobObject(job, exitCode: 1);
                WindowsJobSessionAnchor.NativeMethods.CloseHandle(job);
            });
        }

        if (job != IntPtr.Zero)
        {
            WindowsJobSessionAnchor.NativeMethods.CloseHandle(job);
        }

        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private sealed class Ending(Action end) : IDisposable
    {
        private Action? _end = end;

        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }

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
