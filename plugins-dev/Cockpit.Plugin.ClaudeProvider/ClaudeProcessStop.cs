using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cockpit.Plugin.ClaudeProvider;

// Ends a claude this plugin started (a session, a sign-in, a login check): SIGTERM, two seconds, then the tree kill.
// Under the server image the child is `sudo -u agent` (AC-1464), which keeps the cockpit's real uid and relays a
// SIGTERM to claude; a SIGKILL, all Process.Kill sends, would orphan it. Measured 03-10, aspnet:10.0, sudo 1.9.15p5.
internal static class ClaudeProcessStop
{
    private const int SigTerm = 15;

    public static void Stop(Process process)
    {
        try
        {
            if (process.HasExited || _Terminate(process))
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the signal, or never started.
        }
        catch (Exception exception) when (exception is Win32Exception or AggregateException)
        {
            // AC-1468: the tree below sudo is the agent's, not ours to kill (EPERM). Logged, never thrown out of a teardown.
            Console.Error.WriteLine($"claude-provider: the claude process tree of pid {process.Id} could not be killed: {exception.Message}");
        }
    }

    // True when the SIGTERM ended the process. On the desktop the child is claude itself; Windows has no SIGTERM.
    private static bool _Terminate(Process process) =>
        !OperatingSystem.IsWindows()
        && kill(process.Id, SigTerm) == 0
        && process.WaitForExit(TimeSpan.FromSeconds(2));

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
