using System.ComponentModel;
using System.Diagnostics;

namespace Cockpit.Core.Terminal;

// Opens a shell as administrator (AC-967). Deliberately *not* a pane: elevation goes through
// `ShellExecuteEx` (`UseShellExecute` + the `runas` verb), which gives the elevated process its own console window,
// so this never touches the cockpit's ConPTY and needs no system setting beyond the UAC prompt Windows shows itself.
public static class ElevatedTerminalLauncher
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    // Null when the elevated window started, otherwise a short message for the operator — a declined UAC prompt is
    // the common case and must never fail silently.
    public static string? Launch(ShellDescriptor shell, string? workingDirectory = null)
    {
        if (!IsSupported)
        {
            return "Starting a terminal as administrator is a Windows-only action.";
        }

        var start = new ProcessStartInfo(shell.ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in shell.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(start) is null
                ? "Could not start an elevated terminal."
                : null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return "Elevation was declined — no administrator terminal was started.";
        }
        catch (Exception exception)
        {
            return $"Could not start an elevated terminal: {exception.Message}";
        }
    }
}
