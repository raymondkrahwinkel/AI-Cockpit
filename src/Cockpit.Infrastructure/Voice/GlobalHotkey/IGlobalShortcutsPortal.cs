using Tmds.DBus;

namespace Cockpit.Infrastructure.Voice.GlobalHotkey;

/// <summary>
/// Tmds.DBus proxy contract for <c>org.freedesktop.portal.GlobalShortcuts</c>. Method/watch names map
/// 1:1 onto the D-Bus interface (Tmds.DBus generates the proxy from this shape at runtime) — see the
/// spike this ports, <c>spike1_portal_hotkey.py</c>, for the exact same CreateSession/BindShortcuts/
/// Activated/Deactivated sequence via Gio's lower-level D-Bus API.
/// </summary>
[DBusInterface("org.freedesktop.portal.GlobalShortcuts")]
public interface IGlobalShortcutsPortal : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);

    Task<ObjectPath> BindShortcutsAsync(
        ObjectPath session,
        (string Id, IDictionary<string, object> Options)[] shortcuts,
        string parentWindow,
        IDictionary<string, object> options);

    Task<IDisposable> WatchActivatedAsync(
        Action<(ObjectPath Session, string ShortcutId, ulong Timestamp, IDictionary<string, object> Options)> handler);

    Task<IDisposable> WatchDeactivatedAsync(
        Action<(ObjectPath Session, string ShortcutId, ulong Timestamp, IDictionary<string, object> Options)> handler);
}
