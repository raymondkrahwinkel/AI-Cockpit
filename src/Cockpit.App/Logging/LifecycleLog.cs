using Microsoft.Extensions.Logging;

namespace Cockpit.App.Logging;

/// <summary>
/// The handful of lines that say how a run began and how it ended. A cockpit that is simply gone the next time
/// the operator looks left nothing to read: the log recorded what the app was doing, never that it stopped, so
/// "closed by the operator", "quit from the tray" and "the process was taken out from under us" all looked
/// identical afterwards — an empty tail. Each of the app's own exit routes now writes one line as it goes, and a
/// tail with none of them means nothing in this process asked to stop.
/// </summary>
/// <remarks>
/// A static holder rather than an injected logger because the callers are the places DI cannot reach cleanly: the
/// window's own <c>OnClosing</c>, and <c>Program.Main</c>'s <c>finally</c>, which runs after the lifetime — and in
/// tests (and before <see cref="Use"/> is called) it stays a no-op rather than needing a container at all.
/// </remarks>
internal static class LifecycleLog
{
    private static ILogger? _logger;

    /// <summary>Points the lifecycle lines at the app's log file, once the logger factory exists.</summary>
    internal static void Use(ILoggerFactory loggerFactory) =>
        _logger = loggerFactory.CreateLogger("Cockpit.App.Lifecycle");

    /// <summary>Records one step of the run's beginning or end. A no-op until <see cref="Use"/> has been called.</summary>
    internal static void Write(string message) =>
        _logger?.LogInformation("{Message}", message);
}
