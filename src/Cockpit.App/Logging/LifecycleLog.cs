using Microsoft.Extensions.Logging;

namespace Cockpit.App.Logging;

// Distinguishes each deliberate exit route from an externally killed process by recording how a run ended.
// Static access reaches window closing and Program.Main's post-lifetime finally; before `Use`, it is a no-op.
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
