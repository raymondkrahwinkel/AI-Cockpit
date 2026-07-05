using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

/// <summary>
/// Windows presence detection: idle time from the Win32 <c>GetLastInputInfo</c> and lock state from
/// <see cref="SystemEvents.SessionSwitch"/> (<c>SessionLock</c>/<c>SessionUnlock</c>). The measured
/// idle time and lock flag are handed to the pure <see cref="PresenceDecision"/> kernel, which owns
/// the away/present rule so it stays testable without the P/Invoke.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPresenceDetector : IPresenceDetector, IDisposable
{
    private volatile bool _isLocked;

    public WindowsPresenceDetector()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public PresenceState GetPresence(TimeSpan idleThreshold) =>
        PresenceDecision.Decide(GetIdleTime(), _isLocked, idleThreshold);

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _isLocked = e.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,
            _ => _isLocked,
        };
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>(),
        };

        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both values are the tick count since boot; the unsigned subtraction stays correct across
        // the 32-bit wrap (~49.7 days), which a signed subtraction would get wrong.
        var idleMilliseconds = unchecked((uint)Environment.TickCount - info.LastInputTick);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint LastInputTick;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);
}
