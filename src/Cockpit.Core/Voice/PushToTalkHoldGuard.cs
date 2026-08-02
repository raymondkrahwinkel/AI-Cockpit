namespace Cockpit.Core.Voice;

// Ensures a single physical push-to-talk hold starts exactly one capture, even though the OS/Avalonia
// re-raises `KeyDown` repeatedly (key-repeat) for as long as the hotkey stays pressed. Without
// this guard every repeat would look like a fresh press and restart the microphone capture mid-hold.
public sealed class PushToTalkHoldGuard
{
    private bool _isHolding;

    // True the first time this is called for a hold; false on every repeat while it is still held.
    public bool TryBeginHold()
    {
        if (_isHolding)
        {
            return false;
        }

        _isHolding = true;
        return true;
    }

    // Ends the hold so the next `TryBeginHold` (a fresh key press) succeeds again.
    public void Release() => _isHolding = false;
}
