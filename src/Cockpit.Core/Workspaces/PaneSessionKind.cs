namespace Cockpit.Core.Workspaces;

// For a `PaneKind.AiSession` pane: which factory rebuilds it after a restart (AC-410) — an SDK chat panel or a
// TTY terminal panel. Domain-side mirror of `Cockpit.App.ViewModels.SessionKind`, kept separate so this project
// never references the app layer (same reasoning as `PluginSessionCapabilities` mirroring `SessionCapabilities`).
public enum PaneSessionKind
{
    Sdk,
    Tty,
}
