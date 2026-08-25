namespace Cockpit.Core.Profiles;

// Which route (AC-139) a New session under a `SessionProfile` pre-selects: SDK chat panel or TTY terminal panel.
// The domain-side mirror of `Cockpit.App.ViewModels.SessionKind`, kept separate so this project never references
// the app layer (same reasoning as `Workspaces.PaneSessionKind` mirroring the same enum for a different purpose).
public enum ProfileSessionKind
{
    Sdk,
    Tty,
}
