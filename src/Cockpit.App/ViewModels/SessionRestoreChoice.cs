namespace Cockpit.App.ViewModels;

// What the operator decided to do with a restore offer (AC-410) — the two choices that actually need
// `CockpitViewModel` to start a session. Closing the offer is not a third case here: the banner's Close
// button runs through `SessionPanelViewModel.CloseRequested` directly, the same self-close path a
// TTY's "exit" already uses, so this only has to distinguish the two starts.
public enum SessionRestoreChoice
{
    // Pick the earlier conversation back up — starts with `SessionResume.BySessionId`.
    Resume,

    // Start over — starts with `SessionResume.New`.
    StartFresh,
}
