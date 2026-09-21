using System.ComponentModel;
using System.Diagnostics;

namespace Cockpit.Core.Terminal;

// The one runas start point (AC-967, AC-1335): `ShellExecuteEx` with the `runas` verb, so Windows shows its own
// UAC prompt and no system setting is involved. Shared by the administrator terminal and the elevated command tool.
public static class ElevatedProcessStart
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    // ERROR_CANCELLED: the operator answered the UAC prompt with "No".
    private const int ErrorCancelled = 1223;

    // Null when nothing started: `declined` says whether that was the operator's UAC "No" (the common case) or
    // some other failure, whose text is in `error`.
    public static Process? Start(ProcessStartInfo start, out bool declined, out string? error)
    {
        start.UseShellExecute = true;
        start.Verb = "runas";
        declined = false;
        error = null;

        try
        {
            var process = Process.Start(start);
            if (process is null)
            {
                error = "Windows started nothing.";
            }

            return process;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            declined = true;
            return null;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }
}
