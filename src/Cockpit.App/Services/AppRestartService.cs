using System.Diagnostics;
using Avalonia;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

// AC-1013: Real `IAppRestartService` (#53) launches an independent copy of the process, then reuses the
// app's existing clean-exit path (bug #32, AC-958) via `App.RequestQuit`, so restarting needs no teardown
// logic of its own; both steps are injected as delegates so tests can substitute fakes for either.
internal sealed class AppRestartService : IAppRestartService, ISingletonService
{
    // Marks the launched process as a restart handoff rather than a fresh double-launch. The new instance reads
    // it (in `Program.Main`) and waits for this one to release the single-instance claim, instead of finding
    // it still held and refusing to start with the "already running" notice.
    internal const string RestartArgument = "--restarting";

    private readonly Action _launchNewInstance;
    private readonly Action _shutDownCurrentInstance;

    public AppRestartService()
        : this(_LaunchNewInstance, _ShutDownCurrentInstance)
    {
    }

    internal AppRestartService(Action launchNewInstance, Action shutDownCurrentInstance)
    {
        _launchNewInstance = launchNewInstance;
        _shutDownCurrentInstance = shutDownCurrentInstance;
    }

    public void Restart()
    {
        _launchNewInstance();
        _shutDownCurrentInstance();
    }

    private static void _LaunchNewInstance()
    {
        if (Environment.ProcessPath is not { Length: > 0 } exePath)
        {
            return;
        }

        var startInfo = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
        };

        // GetCommandLineArgs()[0] is the executable path itself (already captured as exePath above);
        // BuildLaunchArguments takes only the arguments that followed it.
        foreach (var arg in BuildLaunchArguments(Environment.GetCommandLineArgs().Skip(1).ToArray()))
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process.Start(startInfo);
    }

    // AC-1013: relaunch arguments are the current ones plus the `RestartArgument` marker, with any existing
    // marker dropped first (so it can't grow unbounded) and `PluginManager.SafeModeArgument` (AC-478) dropped
    // too, since safe mode is a one-shot recovery flag, not something the operator must clear by hand.
    internal static IReadOnlyList<string> BuildLaunchArguments(IReadOnlyList<string> currentArguments) =>
        [.. currentArguments.Where(argument => argument != RestartArgument && argument != PluginManager.SafeModeArgument), RestartArgument];

    private static void _ShutDownCurrentInstance()
    {
        if (Application.Current is App app)
        {
            app.RequestQuit();
        }
    }
}
