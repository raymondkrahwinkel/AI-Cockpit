using Cockpit.Infrastructure.Voice;

namespace Cockpit.App;

// The routes that finish without the cockpit ever starting, in one table so the next one cannot be wired in below
// the single-instance guard by accident: behind it a route stands down against the operator's running cockpit and
// exits 0 with no output and no file (AC-1235). HeadlessRouteStartupOrderTests pins TryRun ahead of that guard.
internal static class HeadlessRoutes
{
    private static readonly (Func<string[], bool> IsRequested, Func<string[], int> Run)[] Routes =
    [
        // Whisper calibration: its one-runtime-per-process native load measures, prints and exits in an isolated
        // child (AC-68).
        (HeadlessCalibration.IsRequested,
            args => HeadlessCalibration.RunAsync(args, CancellationToken.None).GetAwaiter().GetResult()),

        // Dictation, for the same isolation and one reason more: Whisper's native runtime aborts, and a cheap
        // child taking that with it cannot take the desktop down too (AC-174).
        (HeadlessDictation.IsRequested,
            args => HeadlessDictation.RunAsync(args, CancellationToken.None).GetAwaiter().GetResult()),

        // A screenshot, which needs nothing else running: its scenes carry their own view models and never resolve
        // `Program.Services`, so it also writes nothing into the operator's state directory (AC-1235).
        (Screenshotter.IsRequested, Screenshotter.RunFromCommandLine),
    ];

    // The exit code of the route this command line asked for, or false if it asked for none of them.
    public static bool TryRun(string[] args, out int exitCode)
    {
        foreach (var route in Routes)
        {
            if (route.IsRequested(args))
            {
                exitCode = route.Run(args);

                return true;
            }
        }

        exitCode = 0;

        return false;
    }
}
