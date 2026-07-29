namespace Cockpit.Core.Profiles;

/// <summary>
/// Which route (AC-139) a New session under a <see cref="SessionProfile"/> pre-selects: an SDK chat panel or a
/// TTY terminal panel. The domain-side mirror of <c>Cockpit.App.ViewModels.SessionKind</c>, kept separate so this
/// project never has to reference the app layer (same reasoning as <see cref="Workspaces.PaneSessionKind"/>
/// mirroring the same enum for a different purpose).
/// </summary>
public enum ProfileSessionKind
{
    Sdk,
    Tty,
}
