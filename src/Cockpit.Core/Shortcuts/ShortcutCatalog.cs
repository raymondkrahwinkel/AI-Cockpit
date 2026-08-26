namespace Cockpit.Core.Shortcuts;

// One app action's shortcut metadata: its `Label` for the Options list and its `DefaultGesture` (empty = unbound by default).
public sealed record ShortcutDescriptor(ShortcutAction Action, string Label, string DefaultGesture);

// Shortcut labels and defaults live here; strings keep Core UI-framework-free, and blank means unbound.
// Pane, workspace, and zoom navigation use two modifiers so they stay active in the embedded terminal.
// Zoom uses Ctrl+Shift+M: Z steals Redo, Ctrl+B conflicts with shells, and Ctrl+Alt+letter collides with AltGr.
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
        new(ShortcutAction.ToggleZoom, "Toggle zoom", "Ctrl+Shift+M"),
        new(ShortcutAction.CommandPalette, "Command palette", "Ctrl+K"),
        new(ShortcutAction.FocusPaneLeft, "Focus pane left", "Ctrl+Alt+Left"),
        new(ShortcutAction.FocusPaneRight, "Focus pane right", "Ctrl+Alt+Right"),
        new(ShortcutAction.FocusPaneUp, "Focus pane up", "Ctrl+Alt+Up"),
        new(ShortcutAction.FocusPaneDown, "Focus pane down", "Ctrl+Alt+Down"),
        new(ShortcutAction.PreviousSession, "Previous session", "Ctrl+Shift+Up"),
        new(ShortcutAction.NextSession, "Next session", "Ctrl+Shift+Down"),
        new(ShortcutAction.PreviousWorkspace, "Previous workspace", "Ctrl+Shift+Left"),
        new(ShortcutAction.NextWorkspace, "Next workspace", "Ctrl+Shift+Right"),
        // Deliberately unbound: infrequent workspace actions belong in the palette, and defaults would steal shell gestures.
        // Operators can bind them in Options after discovering them there.
        new(ShortcutAction.NewSessionsWorkspace, "New sessions workspace", ""),
        new(ShortcutAction.NewDashboardWorkspace, "New dashboard workspace", ""),
        new(ShortcutAction.CloseWorkspace, "Close workspace", ""),
    ];

    // Keep navigation live in the embedded terminal (Raymond's call); dialog actions remain gated so single-key shell bindings win.
    // Two-modifier switches bypass focused text boxes independently; Ctrl+N/D and Ctrl+Shift+arrow deliberately shadow shell gestures.
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
