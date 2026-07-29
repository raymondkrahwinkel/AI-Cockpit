namespace Cockpit.Core.Workspaces;

/// <summary>
/// For a <see cref="PaneKind.AiSession"/> pane: which factory rebuilds it after a restart (AC-410) — an SDK chat
/// panel or a TTY terminal panel. The domain-side mirror of <c>Cockpit.App.ViewModels.SessionKind</c>, kept
/// separate so this project never has to reference the app layer (same reasoning as
/// <c>Cockpit.Plugins.Abstractions.Sessions.PluginSessionCapabilities</c> mirroring <c>SessionCapabilities</c>).
/// </summary>
public enum PaneSessionKind
{
    Sdk,
    Tty,
}
