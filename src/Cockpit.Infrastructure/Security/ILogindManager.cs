using Tmds.DBus;

namespace Cockpit.Infrastructure.Security;

/// <summary>
/// Tmds.DBus proxy contract for systemd-logind's <c>org.freedesktop.login1.Manager</c> on the system bus (AC-5).
/// The C# type name is ours to choose; the D-Bus identity lives in the attribute, so this reads as "logind" rather
/// than the raw bus name. Method names still map 1:1 onto the D-Bus members (Tmds.DBus generates the proxy from this
/// shape at runtime), so <see cref="GetSessionAsync"/> must stay named for the <c>GetSession</c> member.
/// </summary>
[DBusInterface("org.freedesktop.login1.Manager")]
public interface ILogindManager : IDBusObject
{
    /// <summary>
    /// Turns a logind session id into its object path. Two ids matter here: the operator's own
    /// <c>XDG_SESSION_ID</c>, and the literal <c>"auto"</c> — logind resolves the latter server-side to the caller's
    /// session or, when the caller has none, to the user's display session. That second case is exactly a cockpit
    /// launched from an AppImage or <c>.desktop</c> entry, which runs under <c>app.slice</c> and so belongs to no
    /// session of its own — the situation where the older <c>GetSessionByPID</c> fails with <c>NoSessionForPID</c>.
    /// </summary>
    Task<ObjectPath> GetSessionAsync(string sessionId);
}
