using System.Diagnostics;
using Avalonia;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

/// <summary>
/// Real <see cref="IAppRestartService"/> (#53): launches an independent copy of the running process — same
/// executable, same args, same working directory (<see cref="Environment.ProcessPath"/> /
/// <see cref="Environment.GetCommandLineArgs"/>), which on Windows keeps running once this process exits, no
/// job-object/parent-tracking involved — then reuses the app's existing clean-exit path (bug #32) via
/// <see cref="App.RequestQuit"/>: that sets <see cref="App.IsQuitting"/> and calls the desktop lifetime's
/// <c>Shutdown()</c>, which <c>Program.Main</c>'s <c>finally</c> picks up to dispose the running sessions and
/// hard-exit. Restarting therefore needs no teardown logic of its own.
/// </summary>
/// <remarks>
/// Both steps are constructor-injected as plain delegates (see the internal test constructor) so a test can
/// substitute fakes for the real process spawn and the real app shutdown — neither should actually run in a
/// unit test.
/// </remarks>
internal sealed class AppRestartService : IAppRestartService, ISingletonService
{
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
        // ArgumentList only wants the actual arguments that followed it.
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process.Start(startInfo);
    }

    private static void _ShutDownCurrentInstance()
    {
        if (Application.Current is App app)
        {
            app.RequestQuit();
        }
    }
}
