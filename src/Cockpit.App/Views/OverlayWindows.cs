using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Cockpit.App.Views;

// Two things a cockpit window can ask of the OS about itself, never about anyone else's window: to stay out of
// screen captures, and to let the pointer fall through to whatever lies below. Both are best-effort; a failure
// leaves the window as it was, never broken. Nothing here sends input anywhere (AC-1235).
internal static class OverlayWindows
{
    // `WDA_EXCLUDEFROMCAPTURE`: Windows 10 2004 and later leave the window out of BitBlt and duplication captures.
    private const uint ExcludeFromCaptureAffinity = 0x11;

    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x80000;
    private const long WsExTransparent = 0x20;
    private const uint LwaAlpha = 0x2;

    // AC-2209: only Windows can do this; elsewhere the caller hides the window just before a capture instead.
    public static bool CanExcludeFromCapture => OperatingSystem.IsWindows();

    public static void ExcludeFromCapture(Window window)
    {
        if (!CanExcludeFromCapture || window.TryGetPlatformHandle() is not { } handle)
        {
            return;
        }

        try
        {
            _SetWindowDisplayAffinity(handle.Handle, ExcludeFromCaptureAffinity);
        }
        catch (Exception)
        {
            // Older Windows without the call: the window then shows in a capture, as it did before.
        }
    }

    // Lets clicks pass through the whole window: a layered transparent style on Windows, an empty input shape on X11
    // (ported from the KDE/KWin spike for the voice overlay), and ignoresMouseEvents on macOS.
    public static void MakeClickThrough(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var style = _GetWindowLongPtr(handle.Handle, GwlExStyle).ToInt64();
                _SetWindowLongPtr(handle.Handle, GwlExStyle, new IntPtr(style | WsExLayered | WsExTransparent));
                _SetLayeredWindowAttributes(handle.Handle, 0, 255, LwaAlpha);
            }
            else if (OperatingSystem.IsLinux())
            {
                var display = _XOpenDisplay(IntPtr.Zero);
                if (display == IntPtr.Zero)
                {
                    return;
                }

                const int shapeInput = 2;
                const int shapeSet = 0;
                const int unsorted = 0;
                _XShapeCombineRectangles(display, (ulong)(long)handle.Handle, shapeInput, 0, 0, IntPtr.Zero, 0, shapeSet, unsorted);
                _XFlush(display);
                _XCloseDisplay(display);
            }
            else if (handle is Avalonia.Platform.IMacOSTopLevelPlatformHandle { NSWindow: var nsWindow } && nsWindow != IntPtr.Zero)
            {
                // The NSWindow, not the handle's own NSView: a view does not answer this selector and would abort the process.
                _SendBool(nsWindow, _SelRegisterName("setIgnoresMouseEvents:"), true);
            }
        }
        catch (Exception)
        {
            // Best-effort: a failure leaves the window clickable, never fatal.
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowDisplayAffinity")]
    private static extern bool _SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr _GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr _SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetLayeredWindowAttributes")]
    private static extern bool _SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern IntPtr _XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static extern int _XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XFlush")]
    private static extern int _XFlush(IntPtr display);

    [DllImport("libXext.so.6", EntryPoint = "XShapeCombineRectangles")]
    private static extern void _XShapeCombineRectangles(
        IntPtr display, ulong window, int kind, int xOff, int yOff,
        IntPtr rectangles, int nRects, int op, int ordering);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr _SelRegisterName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void _SendBool(IntPtr receiver, IntPtr selector, bool value);
}
