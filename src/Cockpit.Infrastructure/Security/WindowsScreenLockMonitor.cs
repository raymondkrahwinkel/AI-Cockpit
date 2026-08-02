using System.Runtime.Versioning;
using Microsoft.Win32;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Security;

// Watches Windows session lock/unlock via `SystemEvents.SessionSwitch`
// (`SessionLock`/`SessionUnlock`) for AC-5.
//
// The research weighed this against a bespoke message-only window plus `WTSRegisterSessionNotification`. The
// deciding factor for this codebase is that `WindowsPresenceDetector` already reads the exact same
// `SessionSwitch` source in shipping code and it works: Avalonia's Win32 backend runs a real message pump for
// its own windows, which is what `SystemEvents` needs to deliver. Adding a second, hand-rolled native window
// to detect the same event would be untested surface duplicating a proven one. The tradeoff the research flagged —
// that the pump is Avalonia's internal detail rather than a guaranteed contract — is accepted, and is the same bet
// the presence detector already makes. Windows is not a platform we can live-verify here, so this stays thin.
[SupportedOSPlatform("windows")]
internal sealed class WindowsScreenLockMonitor : IScreenLockMonitor
{
    private bool _started;

    public event EventHandler? Locked;

    public event EventHandler? Unlocked;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _started = true;
        }

        return Task.CompletedTask;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                Locked?.Invoke(this, EventArgs.Empty);
                break;
            case SessionSwitchReason.SessionUnlock:
                Unlocked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _started = false;
        }
    }
}
