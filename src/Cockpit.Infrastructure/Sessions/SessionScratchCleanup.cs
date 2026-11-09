using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Cockpit.Infrastructure.Sessions;

internal static class SessionScratchCleanup
{
    public static Process? CaptureProcess(int? processId)
    {
        try
        {
            if (processId is not > 0)
            {
                return null;
            }
            var process = Process.GetProcessById(processId.Value);
            _ = process.SafeHandle;
            return process;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // AC-2235: Git Bash fixes /tmp (usertemp = TMP of the first MSYS process) for every bash of the user until
    // the last MSYS process exits. If that first process was this session's, deleting anything in it breaks /tmp
    // and the temp files of all other sessions; leave it alone. Probing only costs one bash start per closing session.
    private static bool _IsMsysTmp(string path)
    {
        var bash = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "Git", "bin", "bash.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(bash))
        {
            return false;
        }
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(bash, ["-c", "cygpath -w /tmp"])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            var tmp = probe?.StandardOutput.ReadToEnd().Trim();
            return probe is not null && probe.WaitForExit(5000) && tmp is { Length: > 0 } && SessionScratch.IsInside(tmp, path);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static async Task DeleteAsync(string path, string paneId, IWorktreeManager? worktrees, ILogger? logger, Process? process = null)
    {
        try
        {
            if (process is not null && !process.WaitForExit(5000))
            {
                logger?.LogWarning("Session process is still running; keeping scratch directory '{Path}'.", path);
                return;
            }
            var registered = worktrees is null ? [] : await worktrees.ListAsync().ConfigureAwait(false);
            SessionScratch.Delete(path, paneId, registered.Select(record => record.Path),
                message => logger?.LogInformation("Scratch cleanup on dispose: {Message}", message),
                isSharedTmp: _IsMsysTmp(path));
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Could not remove session scratch directory '{Path}'.", path);
        }
        finally
        {
            process?.Dispose();
        }
    }
}
