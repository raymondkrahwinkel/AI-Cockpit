namespace Cockpit.Core.Shortcuts;

/// <summary>One app action's shortcut metadata: its <see cref="Label"/> for the Options list and its <see cref="DefaultGesture"/> (empty = unbound by default).</summary>
public sealed record ShortcutDescriptor(ShortcutAction Action, string Label, string DefaultGesture);

/// <summary>
/// The single source of truth for the app-action shortcuts: their display labels and default gestures. The
/// defaults use Ctrl+letter (Shift alone is awkward — it collides with typed capitals); a blank default means
/// the action starts unbound. Gestures are stored as strings (e.g. "Ctrl+N", "Ctrl+Shift+P") and parsed to an
/// Avalonia <c>KeyGesture</c> at the view layer, keeping this Core type UI-framework-free.
/// <para>
/// The arrow defaults split by axis, matching what they move through on screen: Ctrl+Up/Down steps the
/// session list (a vertical sidebar), Ctrl+Shift+Left/Right steps the workspace tabs (a horizontal strip).
/// </para>
/// </summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutDescriptor> All { get; } =
    [
        new(ShortcutAction.NewSession, "New session", "Ctrl+N"),
        new(ShortcutAction.DuplicateSession, "Duplicate active session", "Ctrl+D"),
        new(ShortcutAction.ManageProfiles, "Manage profiles", "Ctrl+R"),
        new(ShortcutAction.McpServers, "MCP servers", "Ctrl+M"),
        new(ShortcutAction.PluginStore, "Plugin store", "Ctrl+P"),
        new(ShortcutAction.Options, "Options", "Ctrl+O"),
        new(ShortcutAction.About, "About", ""),
        new(ShortcutAction.ToggleZoom, "Toggle zoom", "Ctrl+B"),
        new(ShortcutAction.CommandPalette, "Command palette", "Ctrl+K"),
        new(ShortcutAction.PreviousSession, "Previous session", "Ctrl+Up"),
        new(ShortcutAction.NextSession, "Next session", "Ctrl+Down"),
        new(ShortcutAction.PreviousWorkspace, "Previous workspace", "Ctrl+Shift+Left"),
        new(ShortcutAction.NextWorkspace, "Next workspace", "Ctrl+Shift+Right"),
    ];

    /// <summary>
    /// The navigation actions that stay live while the operator types in the embedded terminal — switching
    /// session or workspace, creating, and duplicating are exactly what you reach for while driving a running
    /// TUI (Raymond's call). They remain gated in a text box (where an arrow gesture is caret navigation),
    /// while the dialog-opening actions stay gated over the terminal so single-key shell bindings (Ctrl+R
    /// reverse-search, …) reach the shell. Note: with Ctrl+N/Ctrl+D active here, they shadow the shell's
    /// next-history/EOF, and the workspace switch shadows whatever the terminal would do with
    /// Ctrl+Shift+arrow (word selection, in some emulators) — the same deliberate trade.
    /// </summary>
    public static bool StaysActiveInTerminal(ShortcutAction action) =>
        action is ShortcutAction.PreviousSession
            or ShortcutAction.NextSession
            or ShortcutAction.PreviousWorkspace
            or ShortcutAction.NextWorkspace
            or ShortcutAction.NewSession
            or ShortcutAction.DuplicateSession;

    public static string DefaultGesture(ShortcutAction action) =>
        All.First(descriptor => descriptor.Action == action).DefaultGesture;
}
