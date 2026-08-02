using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// The windows on a genuine X11 session (AC-330): `_NET_CLIENT_LIST_STACKING` for the stacking order and
// `XGetGeometry` plus `XTranslateCoordinates` for where each one sits on the screen.
// Registered only for a real X11 session, never under XWayland. From inside an XWayland client this property
// lists other XWayland clients and nothing else — on Plasma 6 nearly every window is a native Wayland toplevel,
// so the picker would offer a handful of the operator's windows and silently omit the rest.
//
// Unverified: there is no X11 session here to run it against. Written to the standard P/Invoke pattern and kept
// thin, with the decisions above it (`ScreenshotSelectionViewModel`) where they are tested — the same
// split `MacScreenLockMonitor` takes for the same reason.
[SupportedOSPlatform("linux")]
internal sealed class X11DesktopWindows : IDesktopWindows
{
    private const string X11 = "libX11.so.6";

    // Enough for any desktop; the property is read in one go rather than paged.
    private const long MaxWindows = 1024;

    public bool IsSupported => true;

    public IReadOnlyList<DesktopWindow> Enumerate()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return [];
        }

        // Xlib's default error handler prints and calls exit(). A window that closes between being listed and
        // being asked about is an ordinary race on a live desktop, and a BadWindow from it would take the whole
        // cockpit down — so the handler is swapped for one that answers nothing and restored afterwards. It is
        // process-global, which is why it goes back.
        var previousHandler = XSetErrorHandler(_IgnoreError);
        try
        {
            var root = XDefaultRootWindow(display);
            var stacking = XInternAtom(display, "_NET_CLIENT_LIST_STACKING", true);
            if (stacking == IntPtr.Zero)
            {
                // A window manager that does not publish the property. Every desktop the cockpit targets does,
                // so this is a bare X server rather than a session to pick windows on.
                return [];
            }

            // The property is bottom-to-top, which is the reverse of what a picker wants: the window under the
            // pointer is the front-most one, and the first match has to be that.
            return _WindowsOf(display, root, stacking)
                .Select(window => _Describe(display, root, window))
                .OfType<DesktopWindow>()
                .Reverse()
                .ToList();
        }
        finally
        {
            XSetErrorHandler(previousHandler);
            XCloseDisplay(display);
        }
    }

    // Held as a field so the GC never collects the delegate while Xlib still holds the function pointer.
    private static readonly XErrorHandler _IgnoreError = (_, _) => 0;

    private static IReadOnlyList<IntPtr> _WindowsOf(IntPtr display, IntPtr root, IntPtr property)
    {
        if (XGetWindowProperty(display, root, property, 0, MaxWindows, false, 33, out _, out var format, out var count, out _, out var data) != 0
            || data == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            // XA_WINDOW comes back as 32-bit values, which Xlib hands over as C longs — eight bytes each on the
            // 64-bit platforms this runs on. Reading them as 4-byte integers would take half of every id.
            if (format != 32)
            {
                return [];
            }

            var windows = new IntPtr[count];
            for (var index = 0; index < (int)count; index++)
            {
                windows[index] = Marshal.ReadIntPtr(data, index * IntPtr.Size);
            }

            return windows;
        }
        finally
        {
            XFree(data);
        }
    }

    private static DesktopWindow? _Describe(IntPtr display, IntPtr root, IntPtr window)
    {
        if (XGetGeometry(display, window, out _, out _, out _, out var width, out var height, out _, out _) == 0)
        {
            return null;
        }

        // Geometry is relative to the parent, which under a reparenting window manager is the frame rather than
        // the root — so the position has to be translated rather than read.
        if (XTranslateCoordinates(display, window, root, 0, 0, out var x, out var y, out _) == 0)
        {
            return null;
        }

        return width > 0 && height > 0
            ? new DesktopWindow { Title = _TitleOf(display, window), Bounds = new CaptureRect(x, y, (int)width, (int)height) }
            : null;
    }

    private static string _TitleOf(IntPtr display, IntPtr window)
    {
        if (XFetchName(display, window, out var name) == 0 || name == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUTF8(name) ?? string.Empty;
        }
        finally
        {
            XFree(name);
        }
    }

    private delegate int XErrorHandler(IntPtr display, IntPtr error);

    [DllImport(X11)]
    private static extern XErrorHandler XSetErrorHandler(XErrorHandler handler);

    [DllImport(X11)]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport(X11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(X11)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(X11)]
    private static extern IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport(X11)]
    private static extern int XGetWindowProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        long offset,
        long length,
        [MarshalAs(UnmanagedType.Bool)] bool delete,
        IntPtr type,
        out IntPtr actualType,
        out int format,
        out ulong count,
        out ulong remaining,
        out IntPtr data);

    [DllImport(X11)]
    private static extern int XGetGeometry(
        IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);

    [DllImport(X11)]
    private static extern int XTranslateCoordinates(
        IntPtr display, IntPtr source, IntPtr destination, int sourceX, int sourceY, out int x, out int y, out IntPtr child);

    [DllImport(X11)]
    private static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr name);

    [DllImport(X11)]
    private static extern int XFree(IntPtr data);
}
