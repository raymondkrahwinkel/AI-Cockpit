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
            SessionScratch.Delete(path, paneId, registered.Select(record => record.Path));
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
