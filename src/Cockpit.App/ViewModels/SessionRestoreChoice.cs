namespace Cockpit.App.ViewModels;

/// <summary>
/// What the operator decided to do with a restore offer (AC-410) — the two choices that actually need
/// <c>CockpitViewModel</c> to start a session. Closing the offer is not a third case here: the banner's Close
/// button runs through <see cref="SessionPanelViewModel.CloseRequested"/> directly, the same self-close path a
/// TTY's "exit" already uses, so this only has to distinguish the two starts.
/// </summary>
public enum SessionRestoreChoice
{
    /// <summary>Pick the earlier conversation back up — starts with <c>SessionResume.BySessionId</c>.</summary>
    Resume,

    /// <summary>Start over — starts with <c>SessionResume.New</c>.</summary>
    StartFresh,
}
