using Cockpit.Core.Abstractions.Assistant;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Arms and disarms the watch that tells the assistant when a pane finishes, sticks or matches a pattern (AC-640).
/// The seam <c>AssistantAgentGateway</c> reaches the watcher through until the watcher itself moves (AC-1375).
/// </summary>
public interface ISessionWatcher
{
    /// <summary>
    /// Arms a watch on <paramref name="paneId"/>, replacing whatever was armed on it; refuses with a reason rather than throws.
    /// </summary>
    Task<AssistantWatchResult> WatchAsync(string paneId, IReadOnlyList<string>? events, int? afterMinutes, string? pattern);

    /// <summary>
    /// Disarms the watch on <paramref name="paneId"/>; false when none was armed.
    /// </summary>
    Task<bool> UnwatchAsync(string paneId);
}
