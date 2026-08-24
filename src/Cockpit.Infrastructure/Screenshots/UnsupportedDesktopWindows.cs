using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// AC-1013 (AC-330): the answer on Wayland, which deliberately hides window locations from clients; the X11
// route (`_NET_CLIENT_LIST_STACKING` under XWayland) only sees other XWayland windows and would omit almost
// everything, which is worse than not offering a picker. Dropped: KWin's `ScreenShot2` as unverified candidate.
internal sealed class UnsupportedDesktopWindows : IDesktopWindows
{
    public bool IsSupported => false;

    public IReadOnlyList<DesktopWindow> Enumerate() => [];
}
