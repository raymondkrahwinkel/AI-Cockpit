using Microsoft.Extensions.Logging;

namespace Cockpit.App.Logging;

// The handful of lines that say how a run began and how it ended. A cockpit that is simply gone the next time
// the operator looks left nothing to read: the log recorded what the app was doing, never that it stopped, so
// "closed by the operator", "quit from the tray" and "the process was taken out from under us" all looked
// identical afterwards — an empty tail. Each of the app's own exit routes now writes one line as it goes, and a
// tail with none of them means nothing in this process asked to stop.
// A static holder rather than an injected logger because the callers are the places DI cannot reach cleanly: the
// window's own `OnClosing`, and `Program.Main`'s `finally`, which runs after the lifetime — and in
// tests (and before `Use` is called) it stays a no-op rather than needing a container at all.
internal static class LifecycleLog
{
    private static ILogger? _logger;

    // Points the lifecycle lines at the app's log file, once the logger factory exists.
    internal static void Use(ILoggerFactory loggerFactory) =>
        _logger = loggerFactory.CreateLogger("Cockpit.App.Lifecycle");

    // Records one step of the run's beginning or end. A no-op until `Use` has been called.
    internal static void Write(string message) =>
        _logger?.LogInformation("{Message}", message);
}
