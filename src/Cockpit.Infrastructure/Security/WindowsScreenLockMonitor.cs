using System.Runtime.Versioning;
using Microsoft.Win32;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Security;

// Watches Windows session lock/unlock via `SystemEvents.SessionSwitch` (`SessionLock`/`SessionUnlock`, AC-5).
// Chosen over a bespoke `WTSRegisterSessionNotification` window because `WindowsPresenceDetector` already
// reads the same source, relying on Avalonia's Win32 message pump — the same bet the detector already makes.
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
