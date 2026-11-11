using System.Diagnostics;
using Cockpit.Infrastructure.BackendApi;

namespace Cockpit.App.Composition;

internal static class RemoteWindowProcess
{
    internal static ProcessStartInfo StartInfo(string server, string? category = null)
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
        if (category is not null)
        {
            start.ArgumentList.Add("--options");
            start.ArgumentList.Add(category);
        }
        return start;
    }

    internal static string? OptionsCategoryFrom(string[] args)
    {
        var index = Array.IndexOf(args, "--options");
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    public static void Open(string server, string? category)
    {
        using var process = Process.Start(StartInfo(server, category)) ?? throw new InvalidOperationException("The remote window did not start.");
        Logging.LifecycleLog.Write($"Remote window for {server} started: pid {process.Id}.");
    }
}
