namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// The working directories the cockpit remembers for its New-session quick-pick (AC-174), exposed to a plugin so it can
/// offer the same folders — a plugin that needs the operator to name a working directory (Autopilot's plan) shows the
/// folders they already use instead of making them retype a path. <see cref="Favorites"/> are the operator's pinned
/// folders (shown first, with a ★), <see cref="Recents"/> the most-recently-used ones (most-recent first). Either list
/// may be empty. A plugin records a newly-chosen folder back with <see cref="ICockpitHost.RememberWorkingPathAsync"/>,
/// so the two surfaces share one history.
/// </summary>
public sealed record PluginRememberedWorkingPaths(IReadOnlyList<string> Favorites, IReadOnlyList<string> Recents)
{
    /// <summary>Nothing remembered yet — what a host with no saved history returns.</summary>
    public static PluginRememberedWorkingPaths Empty { get; } = new([], []);
}
