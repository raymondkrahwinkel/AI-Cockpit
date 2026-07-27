using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// The answer on a desktop that will not say where its windows are (AC-330) — which is Wayland, and on purpose:
/// telling an ordinary client where other applications' windows sit is what it was designed not to do.
/// </summary>
/// <remarks>
/// The X11 route is not a way around it here. This app is Avalonia 12, which has no native Wayland backend, so
/// on Plasma 6 it runs as an XWayland client — and <c>_NET_CLIENT_LIST_STACKING</c> from inside XWayland lists
/// only other XWayland windows. Nearly everything on that desktop is a native Wayland toplevel and would simply
/// be missing, so a picker built on it offers a fraction of the operator's windows, which is worse than offering
/// none.
/// <para>
/// KWin's own <c>org.kde.KWin.ScreenShot2</c> is the one remaining candidate on KDE, and two things about it are
/// inferred rather than established: whether <c>CaptureInteractive</c> shows KWin's own hover-highlight picker,
/// and whether access really is declarative through an installed <c>.desktop</c> entry — which would make one a
/// packaging requirement. Until a spike on a real Plasma session answers both, the mode does not exist here and
/// says so, rather than being a button that quietly does nothing.
/// </para>
/// </remarks>
internal sealed class UnsupportedDesktopWindows : IDesktopWindows
{
    public bool IsSupported => false;

    public IReadOnlyList<DesktopWindow> Enumerate() => [];
}
