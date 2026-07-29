namespace Cockpit.Core.Abstractions.Hotkeys;

/// <summary>
/// Answers the one question neither <see cref="IGlobalHotkeyService"/> backend can (AC-71): is this process the
/// only cockpit instance armed for a given hotkey? A Windows/X11 keyboard hook installs regardless of who else
/// has one — two instances both see every press, silently — and the Wayland portal accepts a bind request from
/// a session without saying whether another session already claimed it. An advisory, cross-process claim is the
/// one mechanism that works the same on every platform and does not depend on either backend telling the truth
/// about a conflict it cannot see.
/// </summary>
public interface IHotkeyExclusivityGuard
{
    /// <summary>
    /// Claims <paramref name="hotkeyId"/> for this process, or returns null when another live process already
    /// holds it. Disposing the result releases the claim — including implicitly, by the process exiting or
    /// crashing, which is what lets a waiting instance pick the hotkey back up without either one restarting.
    /// Calling this again for a hotkey this same process already holds returns a claim for it, rather than
    /// refusing itself.
    /// </summary>
    IDisposable? TryAcquire(string hotkeyId);
}
