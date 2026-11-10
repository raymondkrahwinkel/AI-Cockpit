using System.Diagnostics;
using Cockpit.Infrastructure.BackendApi;

namespace Cockpit.App.Services;

internal static class RemoteWindowProcess
{
    internal static ProcessStartInfo StartInfo(string server)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The Cockpit executable path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        start.ArgumentList.Add(RemoteInstance.Argument);
        start.ArgumentList.Add(server);
        return start;
    }

    public static void Open(string server)
    {
        using var process = Process.Start(StartInfo(server)) ?? throw new InvalidOperationException("The remote window did not start.");
        Logging.LifecycleLog.Write($"Remote window for {server} started: pid {process.Id}.");
    }
}
