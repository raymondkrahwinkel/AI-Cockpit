using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Cockpit.App.Views;

/// <summary>
/// The floating "Listening"/"Transcribing" pill shown while a global push-to-talk hold is active — see
/// <c>VoicePushToTalkCoordinator</c> for what drives its Show/Hide. Borderless, transparent,
/// always-on-top, bottom-centre of the primary screen, and (on Linux/X11) click-through so it never
/// steals focus or blocks the app underneath. Ported from the KDE/KWin spike that proved topmost +
/// positioning + click-through work via XWayland (Iron Law #9: reuse the proven approach as the base
/// rather than reinventing it) — this window reuses that spike's window setup and click-through code
/// almost verbatim.
/// </summary>
public partial class VoiceOverlayWindow : Window
{
    private const int BottomGap = 48;
    private bool _clickThroughApplied;

    public VoiceOverlayWindow()
    {
        InitializeComponent();
        Opened += _OnOpened;
    }

    /// <summary>Re-centres the pill bottom-centre — called before every show to cover a screen/resolution change between holds.</summary>
    public void PositionBottomCenter()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = (int)(Bounds.Width * screen.Scaling);
        var height = (int)(Bounds.Height * screen.Scaling);
        var x = area.X + ((area.Width - width) / 2);
        var y = area.Y + area.Height - height - BottomGap;
        Position = new PixelPoint(x, y);
    }

    private void _OnOpened(object? sender, EventArgs e)
    {
        PositionBottomCenter();
        _TryEnableClickThrough();
    }

    // Best-effort X11 input-shape click-through (Linux/XWayland only, ported from the spike): gives the
    // window an empty input region so pointer events fall through to whatever is beneath it. Applied
    // once — the shape persists on the platform window for its lifetime. A failure here just leaves the
    // pill clickable; it never blocks showing the overlay.
    private void _TryEnableClickThrough()
    {
        if (_clickThroughApplied || !OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var handle = TryGetPlatformHandle();
            if (handle is null)
            {
                return;
            }

            var xid = (ulong)(long)handle.Handle;
            var display = _XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return;
            }

            const int shapeInput = 2;
            const int shapeSet = 0;
            const int unsorted = 0;
            _XShapeCombineRectangles(display, xid, shapeInput, 0, 0, IntPtr.Zero, 0, shapeSet, unsorted);
            _XFlush(display);
            _XCloseDisplay(display);
            _clickThroughApplied = true;
        }
        catch (Exception)
        {
            // Click-through is best-effort: a failure here just leaves the pill clickable, never fatal.
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr _XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int _XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int _XFlush(IntPtr display);

    [DllImport("libXext.so.6")]
    private static extern void _XShapeCombineRectangles(
        IntPtr display, ulong window, int kind, int xOff, int yOff,
        IntPtr rectangles, int nRects, int op, int ordering);
}
