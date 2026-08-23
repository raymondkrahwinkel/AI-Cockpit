namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// The working directories the cockpit remembers for its New-session quick-pick (AC-174), exposed so a plugin
/// can offer the same folders instead of making the operator retype a path.
/// </summary>
/// <remarks>
/// <see cref="Favorites"/> are the operator's pinned folders; <see cref="Recents"/> the most-recently-used ones.
/// Either list may be empty. Record a newly-chosen folder back with
/// <see cref="ICockpitHost.RememberWorkingPathAsync"/> so the two surfaces share one history.
/// </remarks>
public sealed record PluginRememberedWorkingPaths(IReadOnlyList<string> Favorites, IReadOnlyList<string> Recents)
{
    /// <summary>
    /// Nothing remembered yet — what a host with no saved history returns.
    /// </summary>
    public static PluginRememberedWorkingPaths Empty { get; } = new([], []);
}
