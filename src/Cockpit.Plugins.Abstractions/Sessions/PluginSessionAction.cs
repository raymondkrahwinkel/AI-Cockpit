using Material.Icons;

namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Something a plugin can do <em>to one session</em>, offered from that session's own menu (#: session actions).
/// A plugin that has nothing to show contributes nothing.
/// </summary>
/// <param name="Title">
/// What it says in the menu: "Track a YouTrack issue…".
/// </param>
/// <param name="Icon">
/// A single character or emoji, or empty. Used when <see cref="IconKind"/> is null.
/// </param>
/// <param name="Invoke">
/// Runs it, for the session it was opened from — on the UI thread.
/// </param>
public sealed record PluginSessionAction(string Title, string Icon, Action<IPluginSessionContext> Invoke)
{
    /// <summary>
    /// A bundled vector icon for the menu item, preferred over <see cref="Icon"/> when set — so the action reads as
    /// part of the theme instead of an emoji the host renders in the machine's own font. Null keeps the string.
    /// </summary>
    public MaterialIconKind? IconKind { get; init; }
}
