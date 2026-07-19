using Tmds.DBus;

namespace Cockpit.Infrastructure.Security;

/// <summary>
/// Tmds.DBus proxy contract for systemd-logind's <c>org.freedesktop.login1.Manager</c> on the system bus. Only the
/// one call AC-5 needs is modelled: turning this process's PID into its session's object path, so the lock/unlock
/// signals can be watched on exactly the operator's session. Method name maps 1:1 onto the D-Bus interface
/// (Tmds.DBus generates the proxy from this shape at runtime); see the systemd <c>org.freedesktop.login1</c>
/// man-page.
/// </summary>
[DBusInterface("org.freedesktop.login1.Manager")]
public interface ILogin1Manager : IDBusObject
{
    Task<ObjectPath> GetSessionByPIDAsync(uint pid);
}

/// <summary>
/// Tmds.DBus proxy contract for a single <c>org.freedesktop.login1.Session</c>. The desktop's lock integration
/// raises the parameterless <c>Lock</c>/<c>Unlock</c> signals (a bare signal maps onto an <see cref="Action"/>
/// handler) when the session locks and unlocks — the DE-independent source AC-5 relies on. <c>LockedHint</c> is the
/// current locked state as a property, used once at startup to catch a screen that was already locked before the
/// watch was in place.
/// </summary>
[DBusInterface("org.freedesktop.login1.Session")]
public interface ILogin1Session : IDBusObject
{
    Task<IDisposable> WatchLockAsync(Action handler);

    Task<IDisposable> WatchUnlockAsync(Action handler);

    Task<T> GetAsync<T>(string prop);
}
