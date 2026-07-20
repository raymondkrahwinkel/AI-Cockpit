namespace Cockpit.Core.Shortcuts;

/// <summary>One app action's shortcut metadata: its <see cref="Label"/> for the Options list and its <see cref="DefaultGesture"/> (empty = unbound by default).</summary>
public sealed record ShortcutDescriptor(ShortcutAction Action, string Label, string DefaultGesture);

/// <summary>
/// The single source of truth for the app-action shortcuts: their display labels and default gestures. The
/// defaults use Ctrl+letter (Shift alone is awkward — it collides with typed capitals); a blank default means
/// the action starts unbound. Gestures are stored as strings (e.g. "Ctrl+N", "Ctrl+Shift+P") and parsed to an
/// Avalonia <c>KeyGesture</c> at the view layer, keeping this Core type UI-framework-free.
/// <para>
/// Ctrl+Alt+arrow moves spatially between panes in the session grid — the focus goes to the pane in that
/// geometric direction. Ctrl+Shift+Up/Down steps the session list, Ctrl+Shift+Left/Right steps the workspace
/// tabs. All three navigation gestures use two modifiers on purpose: a two-modifier gesture fires even while
/// the operator is typing in the embedded terminal, so the plain Ctrl+Left/Right is left to the shell for its
/// word-wise movement.
/// </para>
/// </summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutDescriptor> All { get; } =
    [
        new(ShortcutAction.NewSession, "New session", "Ctrl+N"),
        // Unbound by default (palette-only): a terminal is opened far less often than a session, and every free
        // Ctrl+letter is one the shell wants — so it earns a palette entry beside New session, and an operator who
        // opens terminals often binds it in Options.
        new(ShortcutAction.NewTerminal, "New terminal", ""),
        new(ShortcutAction.DuplicateSession, "Duplicate active session", "Ctrl+D"),
        new(ShortcutAction.ManageProfiles, "Manage profiles", "Ctrl+R"),
        new(ShortcutAction.McpServers, "MCP servers", "Ctrl+M"),
        new(ShortcutAction.PluginStore, "Plugin store", "Ctrl+P"),
        new(ShortcutAction.Options, "Options", "Ctrl+O"),
        new(ShortcutAction.About, "About", ""),
        new(ShortcutAction.ToggleZoom, "Toggle zoom", "Ctrl+B"),
        new(ShortcutAction.CommandPalette, "Command palette", "Ctrl+K"),
        new(ShortcutAction.FocusPaneLeft, "Focus pane left", "Ctrl+Alt+Left"),
        new(ShortcutAction.FocusPaneRight, "Focus pane right", "Ctrl+Alt+Right"),
        new(ShortcutAction.FocusPaneUp, "Focus pane up", "Ctrl+Alt+Up"),
        new(ShortcutAction.FocusPaneDown, "Focus pane down", "Ctrl+Alt+Down"),
        new(ShortcutAction.PreviousSession, "Previous session", "Ctrl+Shift+Up"),
        new(ShortcutAction.NextSession, "Next session", "Ctrl+Shift+Down"),
        new(ShortcutAction.PreviousWorkspace, "Previous workspace", "Ctrl+Shift+Left"),
        new(ShortcutAction.NextWorkspace, "Next workspace", "Ctrl+Shift+Right"),
        // Unbound on purpose. These are things you do to a workspace once and then live with, so they earn a
        // place in the palette rather than a gesture — and every default gesture handed out here is one taken
        // from the shell underneath. An operator who wants one binds it in Options; the palette is where they
        // find out it exists at all.
        new(ShortcutAction.NewSessionsWorkspace, "New sessions workspace", ""),
        new(ShortcutAction.NewDashboardWorkspace, "New dashboard workspace", ""),
        new(ShortcutAction.CloseWorkspace, "Close workspace", ""),
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
