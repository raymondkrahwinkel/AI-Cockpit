namespace Cockpit.Core.Shortcuts;

// One app action's shortcut metadata: its `Label` for the Options list and its `DefaultGesture` (empty = unbound by default).
public sealed record ShortcutDescriptor(ShortcutAction Action, string Label, string DefaultGesture);

// The single source of truth for the app-action shortcuts: their display labels and default gestures. The
// defaults use Ctrl+letter (Shift alone is awkward — it collides with typed capitals); a blank default means
// the action starts unbound. Gestures are stored as strings (e.g. "Ctrl+N", "Ctrl+Shift+P") and parsed to an
// Avalonia `KeyGesture` at the view layer, keeping this Core type UI-framework-free.
//
// Ctrl+Alt+arrow moves spatially between panes in the session grid — the focus goes to the pane in that
// geometric direction. Ctrl+Shift+Up/Down steps the session list, Ctrl+Shift+Left/Right steps the workspace
// tabs. All three navigation gestures use two modifiers on purpose: a two-modifier gesture fires even while
// the operator is typing in the embedded terminal, so the plain Ctrl+Left/Right is left to the shell for its
// word-wise movement. Ctrl+Shift+M (toggle zoom) joins that group for the same reason and needs it most: a
// zoomed pane is precisely the moment the terminal has focus, so a one-modifier gesture would reach the
// shell instead — which is where the old Ctrl+B default went (tmux prefix, readline backward-char).
//
// Two modifiers is a precedence rule, not a safety one: such a gesture is taken before a focused text box
// sees it too. That rules out two tempting chords for zoom. Ctrl+Shift+Z reads best (tmux zooms with z) but
// is the platform's second Redo chord, so it would eat Redo in the prompt field. And Ctrl+Alt+&lt;letter&gt; is
// unavailable for any letter: AltGr is reported as Ctrl+Alt on Windows and Linux, so Ctrl+Alt+Z is how an
// ISO-layout operator types a character (æ on US-International, « on Dutch) — see the AltGr handling in
// `Exclr8.Terminal.TerminalControl.OnKeyDown`. The existing Ctrl+Alt+arrows escape that only because
// arrows produce no character. M is for maximise: no platform gesture, no other default, no plugin shortcut,
// and no AltGr chord either, since it carries no Alt. The shell would read Ctrl+M as a carriage return, but
// nobody presses a chord to send Enter — and it never gets there, since this gesture is taken above the terminal.
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
        // Unbound on purpose. These are things you do to a workspace once and then live with, so they earn a
        // place in the palette rather than a gesture — and every default gesture handed out here is one taken
        // from the shell underneath. An operator who wants one binds it in Options; the palette is where they
        // find out it exists at all.
        new(ShortcutAction.NewSessionsWorkspace, "New sessions workspace", ""),
        new(ShortcutAction.NewDashboardWorkspace, "New dashboard workspace", ""),
        new(ShortcutAction.CloseWorkspace, "Close workspace", ""),
    ];

    // The navigation actions that stay live while the operator types in the embedded terminal — switching
    // session or workspace, creating, and duplicating are exactly what you reach for while driving a running
    // TUI (Raymond's call). This flag alone leaves them gated in a text box (where an arrow gesture is caret
    // navigation), though it only decides that for the one-modifier members: the session and workspace
    // switches carry two modifiers, and the dispatch gate takes such a gesture ahead of a focused text box
    // regardless of this list. The dialog-opening actions stay gated over the terminal so single-key shell
    // bindings (Ctrl+R reverse-search, …) reach the shell. Note: with Ctrl+N/Ctrl+D active here, they shadow
    // the shell's next-history/EOF, and the workspace switch shadows whatever the terminal would do with
    // Ctrl+Shift+arrow (word selection, in some emulators) — the same deliberate trade.
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
