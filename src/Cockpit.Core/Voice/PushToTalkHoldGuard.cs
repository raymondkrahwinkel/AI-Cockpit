namespace Cockpit.Core.Voice;

/// <summary>
/// Ensures a single physical push-to-talk hold starts exactly one capture, even though the OS/Avalonia
/// re-raises <c>KeyDown</c> repeatedly (key-repeat) for as long as the hotkey stays pressed. Without
/// this guard every repeat would look like a fresh press and restart the microphone capture mid-hold.
/// </summary>
public sealed class PushToTalkHoldGuard
{
    private bool _isHolding;

    /// <summary>True the first time this is called for a hold; false on every repeat while it is still held.</summary>
    public bool TryBeginHold()
    {
        if (_isHolding)
        {
            return false;
        }

        _isHolding = true;
        return true;
    }

    /// <summary>Ends the hold so the next <see cref="TryBeginHold"/> (a fresh key press) succeeds again.</summary>
    public void Release() => _isHolding = false;
}
