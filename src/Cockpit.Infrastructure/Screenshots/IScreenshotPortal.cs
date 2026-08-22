using Tmds.DBus;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Tmds.DBus proxy contract for <c>org.freedesktop.portal.Screenshot</c>. The method name maps 1:1 onto the
/// D-Bus interface (Tmds.DBus generates the proxy from this shape at runtime), the same way
/// <c>IGlobalShortcutsPortal</c> does for push-to-talk.
/// </summary>
[DBusInterface("org.freedesktop.portal.Screenshot")]
public interface IScreenshotPortal : IDBusObject
{
    /// <summary>
    /// Asks the desktop for a screenshot. With <c>interactive: false</c> the compositor reads every display
    /// itself and hands back one image with no UI in the way (AC-326) — the desktop prompts once for consent
    /// and remembers it. Returns a Request path; the image's <c>uri</c> arrives on that Request's Response signal.
    /// </summary>
    Task<ObjectPath> ScreenshotAsync(string parentWindow, IDictionary<string, object> options);

    /// <summary>
    /// Reads one of the interface's properties — <c>version</c> is the one that matters, since it is both the
    /// answer to "can this desktop capture at all" (an absent interface throws here) and to "which of the
    /// portal's capabilities exist", the window <c>target</c> option needing v3 (AC-330).
    /// </summary>
    Task<T> GetAsync<T>(string prop);
}
