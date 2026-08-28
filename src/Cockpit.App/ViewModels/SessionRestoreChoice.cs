namespace Cockpit.App.ViewModels;

// Closing the offer is not a third case here: the banner's Close button runs through
// `SessionPanelViewModel.CloseRequested` directly, the same self-close path a TTY's "exit" already uses, so this only
// has to distinguish the two starts (AC-410).
public enum SessionRestoreChoice
{
    // Pick the earlier conversation back up — starts with `SessionResume.BySessionId`.
    Resume,

    // Start over — starts with `SessionResume.New`.
    StartFresh,
}
