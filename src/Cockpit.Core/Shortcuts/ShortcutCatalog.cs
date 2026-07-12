namespace Cockpit.Core.Shortcuts;

/// <summary>One app action's shortcut metadata: its <see cref="Label"/> for the Options list and its <see cref="DefaultGesture"/> (empty = unbound by default).</summary>
public sealed record ShortcutDescriptor(ShortcutAction Action, string Label, string DefaultGesture);

/// <summary>
/// The single source of truth for the app-action shortcuts: their display labels and default gestures. The
/// defaults use Ctrl+letter (Shift alone is awkward — it collides with typed capitals); a blank default means
/// the action starts unbound. Gestures are stored as strings (e.g. "Ctrl+N", "Ctrl+Shift+P") and parsed to an
/// Avalonia <c>KeyGesture</c> at the view layer, keeping this Core type UI-framework-free.
/// </summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutDescriptor> All { get; } =
    [
        new(ShortcutAction.NewSession, "New session", "Ctrl+N"),
        new(ShortcutAction.ManageProfiles, "Manage profiles", "Ctrl+R"),
        new(ShortcutAction.McpServers, "MCP servers", "Ctrl+M"),
        new(ShortcutAction.PluginStore, "Plugin store", "Ctrl+P"),
        new(ShortcutAction.Options, "Options", "Ctrl+O"),
        new(ShortcutAction.About, "About", ""),
        new(ShortcutAction.ToggleZoom, "Toggle zoom", "Ctrl+B"),
        new(ShortcutAction.SearchTranscripts, "Search transcripts", "Ctrl+F"),
        new(ShortcutAction.CommandPalette, "Command palette", "Ctrl+K"),
    ];

    public static string DefaultGesture(ShortcutAction action) =>
        All.First(descriptor => descriptor.Action == action).DefaultGesture;
}
