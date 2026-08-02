namespace Cockpit.Core.Workspaces;

// For a `PaneKind.AiSession` pane: which factory rebuilds it after a restart (AC-410) — an SDK chat
// panel or a TTY terminal panel. The domain-side mirror of `Cockpit.App.ViewModels.SessionKind`, kept
// separate so this project never has to reference the app layer (same reasoning as
// `Cockpit.Plugins.Abstractions.Sessions.PluginSessionCapabilities` mirroring `SessionCapabilities`).
public enum PaneSessionKind
{
    Sdk,
    Tty,
}
