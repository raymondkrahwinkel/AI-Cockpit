using Microsoft.Extensions.Logging;
using Tmds.DBus;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Security;

// Watches systemd-logind for this session's lock/unlock on the D-Bus system bus (AC-5). This is the
// desktop-environment-independent source the research recommended: GNOME, KDE and anything else that integrates with
// logind expose the operator's lock state as the session's `LockedHint` property.
//
// Two things make this work from a real desktop launch, both of which the first cut got wrong. First, the session is
// resolved from `XDG_SESSION_ID` (falling back to logind's `"auto"`), not from this process's PID: a cockpit
// started as an AppImage or `.desktop` entry runs under `app.slice` and belongs to no session of its own, so
// `GetSessionByPID` answered `NoSessionForPID` and the feature never came up. Second, it reacts to
// `LockedHint` via `PropertiesChanged` rather than the session's `Lock` signal, which GNOME raises only
// for `loginctl lock-session` and not for an interactive Super+L.
//
// Best-effort by nature: on a minimal window manager with no logind lock integration nothing sets the hint, and there
// is then no portable notification to have. A connection or lookup that fails is logged once and left — the app keeps
// running with the feature simply inert, never crashed. This half cannot be unit-tested (it needs a live system bus
// and a real desktop lock), so it is deliberately thin; the gate that decides what a lock means lives in the testable
// coordinator above it. Raymond live-verifies this on Linux.
internal sealed class LinuxScreenLockMonitor(ILogger<LinuxScreenLockMonitor> logger) : IScreenLockMonitor
{
    private const string BusName = "org.freedesktop.login1";
    private const string LockedHintProperty = "LockedHint";
    private static readonly ObjectPath ManagerPath = new("/org/freedesktop/login1");

    private Connection? _connection;
    private IDisposable? _propertyWatch;

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

            var manager = _connection.CreateProxy<ILogindManager>(BusName, ManagerPath);
            var sessionPath = await _ResolveDisplaySessionPathAsync(manager).ConfigureAwait(false);

            var session = _connection.CreateProxy<ILogindSession>(BusName, sessionPath);
            _propertyWatch = await session.WatchPropertiesAsync(_OnPropertiesChanged).ConfigureAwait(false);

            // Catch a screen that was already locked before the watch went up: without this a cockpit unlocked while
            // the desktop happened to be locked would sit unlocked behind the lock screen until the next lock cycle.
            // A property read that fails (older logind, a denied read) is not worth failing the whole registration.
            try
            {
                if (await session.GetAsync<bool>(LockedHintProperty).ConfigureAwait(false))
                {
                    _RaiseLocked();
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not read the initial LockedHint from logind; relying on live property changes only.");
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

    // Resolves the operator's login session, tolerating a launch from an app-scope where the process has no session
    // of its own. `XDG_SESSION_ID` is a plain hashmap lookup in logind, so it works regardless of cgroup; a stale
    // or absent id falls through to `"auto"`, which logind maps server-side to the user's display session.
    private async Task<ObjectPath> _ResolveDisplaySessionPathAsync(ILogindManager manager)
    {
        var sessionId = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
        if (!string.IsNullOrEmpty(sessionId))
        {
            try
            {
                return await manager.GetSessionAsync(sessionId).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "XDG_SESSION_ID {SessionId} did not resolve to a logind session; falling back to the display session.", sessionId);
            }
        }

        return await manager.GetSessionAsync("auto").ConfigureAwait(false);
    }

    private void _OnPropertiesChanged(PropertyChanges changes)
    {
        foreach (var property in changes.Changed)
        {
            if (property.Key != LockedHintProperty)
            {
                continue;
            }

            if (property.Value is bool locked)
            {
                if (locked)
                {
                    _RaiseLocked();
                }
                else
                {
                    _RaiseUnlocked();
                }
            }
        }
    }

    private void _RaiseLocked() => Locked?.Invoke(this, EventArgs.Empty);

    private void _RaiseUnlocked() => Unlocked?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _propertyWatch?.Dispose();
        _connection?.Dispose();
        _propertyWatch = null;
        _connection = null;
    }
}
