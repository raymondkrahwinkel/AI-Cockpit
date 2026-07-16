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
    /// <summary>
    /// Marks the launched process as a restart handoff rather than a fresh double-launch. The new instance reads
    /// it (in <c>Program.Main</c>) and waits for this one to release the single-instance claim, instead of finding
    /// it still held and refusing to start with the "already running" notice.
    /// </summary>
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

    /// <summary>
    /// The arguments for the relaunched process: the current ones, plus the <see cref="RestartArgument"/> marker.
    /// Any marker already there (this instance was itself started by a restart) is dropped first, so restart after
    /// restart carries exactly one and the argument list cannot grow without bound.
    /// </summary>
    internal static IReadOnlyList<string> BuildLaunchArguments(IReadOnlyList<string> currentArguments) =>
        [.. currentArguments.Where(argument => argument != RestartArgument), RestartArgument];

    private static void _ShutDownCurrentInstance()
    {
        if (Application.Current is App app)
        {
            app.RequestQuit();
        }
    }
}
