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
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
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
