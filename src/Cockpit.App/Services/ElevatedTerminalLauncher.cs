using System.ComponentModel;
using System.Diagnostics;
using Cockpit.Core.Terminal;

namespace Cockpit.App.Services;

internal static class ElevatedTerminalLauncher
{
    internal static bool IsSupported => OperatingSystem.IsWindows();

    internal static string? Launch(ShellDescriptor shell, string? workingDirectory = null)
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
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return "Elevation was declined — no administrator terminal was started.";
        }
        catch (Exception exception)
        {
            return $"Could not start an elevated terminal: {exception.Message}";
        }
    }
}
