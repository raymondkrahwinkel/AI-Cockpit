using Microsoft.Extensions.Logging;
using Tmds.DBus;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Security;

/// <summary>
/// Watches systemd-logind for this session's lock/unlock on the D-Bus system bus (AC-5). This is the
/// desktop-environment-independent source the research recommended: GNOME, KDE, Xfce and anything else that drives
/// its lock screen through logind raises <c>Session.Lock</c>/<c>Unlock</c> on the operator's session object, which
/// is what <c>loginctl lock-session</c> also rides. The session is found from this process's own PID via
/// <c>Manager.GetSessionByPID</c>, so a machine with several sessions still watches the right one.
/// <para>
/// Best-effort by nature: on a minimal window manager with no logind lock integration nothing raises these signals,
/// and there is then no portable notification to have. A connection or lookup that fails is logged once and left —
/// the app keeps running with the feature simply inert, never crashed. This half cannot be unit-tested (it needs a
/// live system bus and a real desktop lock), so it is deliberately thin; the gate that decides what a lock means
/// lives in the testable coordinator above it. Raymond live-verifies this on Linux.
/// </para>
/// </summary>
internal sealed class LinuxScreenLockMonitor(ILogger<LinuxScreenLockMonitor> logger) : IScreenLockMonitor
{
    private const string BusName = "org.freedesktop.login1";
    private static readonly ObjectPath ManagerPath = new("/org/freedesktop/login1");

    private Connection? _connection;
    private IDisposable? _lockWatch;
    private IDisposable? _unlockWatch;

    public event EventHandler? Locked;

    public event EventHandler? Unlocked;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            return;
        }

        try
        {
            _connection = new Connection(Address.System);
            await _connection.ConnectAsync().ConfigureAwait(false);

            var manager = _connection.CreateProxy<ILogin1Manager>(BusName, ManagerPath);
            var sessionPath = await manager.GetSessionByPIDAsync((uint)Environment.ProcessId).ConfigureAwait(false);

            var session = _connection.CreateProxy<ILogin1Session>(BusName, sessionPath);
            _lockWatch = await session.WatchLockAsync(_OnLock).ConfigureAwait(false);
            _unlockWatch = await session.WatchUnlockAsync(_OnUnlock).ConfigureAwait(false);

            // Catch a screen that was already locked before the watch went up: without this a cockpit unlocked while
            // the desktop happened to be locked would sit unlocked behind the lock screen until the next lock cycle.
            // A property read that fails (older logind, a denied read) is not worth failing the whole registration.
            try
            {
                if (await session.GetAsync<bool>("LockedHint").ConfigureAwait(false))
                {
                    _OnLock();
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not read the initial LockedHint from logind; relying on live signals only.");
            }

            logger.LogInformation("Screen-lock detection registered via systemd-logind for session {Session}.", sessionPath);
        }
        catch (Exception exception)
        {
            // No logind, no session bus route, a sandbox without the socket — the feature is unavailable on this
            // box, which is a thing to note once and move past, not to take the launch down over.
            logger.LogWarning(exception, "Screen-lock detection is unavailable via systemd-logind; the cockpit will not lock with the OS on this session.");
            _connection?.Dispose();
            _connection = null;
        }
    }

    private void _OnLock() => Locked?.Invoke(this, EventArgs.Empty);

    private void _OnUnlock() => Unlocked?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _lockWatch?.Dispose();
        _unlockWatch?.Dispose();
        _connection?.Dispose();
        _lockWatch = null;
        _unlockWatch = null;
        _connection = null;
    }
}
