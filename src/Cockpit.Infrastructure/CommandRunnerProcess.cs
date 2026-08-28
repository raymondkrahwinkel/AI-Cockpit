using System.ComponentModel;
using System.Diagnostics;

namespace Cockpit.Infrastructure;

internal static class CommandRunnerProcess
{
    // A refused kill can keep a pipe open, so abandon a read after a short grace instead of waiting forever.
    private static readonly TimeSpan ReadGrace = TimeSpan.FromSeconds(5);

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
            childFiles = Directory.GetFiles($"/proc/{parentId}/task", "children");
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
