namespace Cockpit.Core.Workspaces;

/// <summary>
/// What one pane in the grid holds. The grid itself is unaware of the difference — it places rectangles —
/// but the kind gates the AI-only chrome (permission mode, resume, session-header contributions) so a
/// terminal or a widget never shows controls that mean nothing to it, and it decides which
/// <see cref="WorkspaceType"/> may host the pane (see <see cref="WorkspaceTypeRules"/>).
/// </summary>
public enum PaneKind
{
    /// <summary>A Claude/provider session — the pane the cockpit started life as.</summary>
    AiSession,

    /// <summary>A plain shell in the same pty the AI TTY uses.</summary>
    Terminal,

    /// <summary>A plugin-contributed widget (see <c>WidgetRegistration</c>).</summary>
    Widget,
}
