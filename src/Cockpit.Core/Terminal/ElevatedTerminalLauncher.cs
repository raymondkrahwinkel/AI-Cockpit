using System.Diagnostics;

namespace Cockpit.Core.Terminal;

// Opens a shell as administrator (AC-967). Deliberately *not* a pane: elevation goes through
// `ElevatedProcessStart` (`ShellExecuteEx` + `runas`), which gives the elevated process its own console window,
// so this never touches the cockpit's ConPTY and needs no system setting beyond the UAC prompt Windows shows itself.
public static class ElevatedTerminalLauncher
{
    public static bool IsSupported => ElevatedProcessStart.IsSupported;

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
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in shell.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (ElevatedProcessStart.Start(start, out var declined, out var error) is not null)
        {
            return null;
        }

        return declined
            ? "Elevation was declined — no administrator terminal was started."
            : $"Could not start an elevated terminal: {error}";
    }
}
