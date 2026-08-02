namespace Cockpit.Core.Workspaces;

// What one pane in the grid holds. The grid itself is unaware of the difference — it places rectangles —
// but the kind gates the AI-only chrome (permission mode, resume, session-header contributions) so a
// terminal or a widget never shows controls that mean nothing to it, and it decides which
// `WorkspaceType` may host the pane (see `WorkspaceTypeRules`).
public enum PaneKind
{
    // A Claude/provider session — the pane the cockpit started life as.
    AiSession,

    // A plain shell in the same pty the AI TTY uses.
    Terminal,

    // A plugin-contributed widget (see `WidgetRegistration`).
    Widget,
}
