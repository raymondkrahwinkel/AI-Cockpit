namespace Cockpit.Core.Workspaces;

// What one pane holds. The grid places rectangles unaware of this; kind gates the AI-only chrome (permission
// mode, resume, session header) so a terminal/widget never shows controls that mean nothing to it, and it
// decides which `WorkspaceType` may host the pane (see `WorkspaceTypeRules`).
public enum PaneKind
{
    // A Claude/provider session — the pane the cockpit started life as.
    AiSession,

    // A plain shell in the same pty the AI TTY uses.
    Terminal,

    // A plugin-contributed widget (see `WidgetRegistration`).
    Widget,
}
