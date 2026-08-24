using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// AC-1013 (AC-327): reads via GDI `BitBlt` + `EnumDisplayMonitors`, not DXGI Desktop Duplication — duplication
// suits a frame stream and costs a native dependency, but lands on the same pixels for one still, and both
// equally respect `WDA_EXCLUDEFROMCAPTURE`. Coordinates share the same enumeration as the pixels (see AC-326).
[SupportedOSPlatform("windows")]
internal sealed class Win32ScreenReader : IWindowsScreenReader
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const int SrcCopy = 0x00CC0020;

    // Includes layered windows in the blit. Without it a window drawn with transparency is simply missing from the capture.
    private const int CaptureBlt = 0x40000000;

    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;

    // What `GetDpiForMonitor` is asked for: the factor this monitor's windows are actually scaled by.
    private const int MdtEffectiveDpi = 0;

    // The DPI at which Windows reports no scaling. A monitor's factor is its DPI over this.
    private const double UnscaledDpi = 96d;

    // `PROCESS_PER_MONITOR_DPI_AWARE` — the process is told each monitor's real DPI rather than the primary's.
    private const int ProcessPerMonitorDpiAware = 2;

    public bool IsPerMonitorDpiAware =>
        GetProcessDpiAwareness(IntPtr.Zero, out var awareness) == 0 && awareness == ProcessPerMonitorDpiAware;

    public WindowsScreenLayout ReadLayout()
    {
        var displays = new List<DesktopDisplay>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (_Describe(monitor) is { } display)
            {
                displays.Add(display);
            }

            return true;
        }, IntPtr.Zero);

        return new WindowsScreenLayout
        {
            VirtualBounds = new CaptureRect(
                GetSystemMetrics(SmXVirtualScreen),
                GetSystemMetrics(SmYVirtualScreen),
                GetSystemMetrics(SmCxVirtualScreen),
                GetSystemMetrics(SmCyVirtualScreen)),
            Displays = displays,
        };
    }

    public byte[] CapturePng(CaptureRect bounds)
    {
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows would not hand out a device context for the screen.");
        }

        try
        {
            return _CopyFrom(screen, bounds);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // Test seam: the same copy against a device context a test drew itself. Row order and the bitmap handover
    // to `GetDIBits` are the two ways this can be wrong while still returning a perfectly valid PNG of
    // exactly the right size — so they need a source whose pixels are known, which the desktop's never are.
    internal byte[] CopyFromForTest(IntPtr source, CaptureRect bounds) => _CopyFrom(source, bounds);

    private static byte[] _CopyFrom(IntPtr screen, CaptureRect bounds)
    {
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("Windows would not allocate a bitmap the size of the screen.");
            }

            var previous = SelectObject(memory, bitmap);
            var copied = BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SrcCopy | CaptureBlt);

            // Puts the device context's own bitmap back. Not needed against a black image (Windows reads it
            // anyway despite GetDIBits' docs) but required for the handle below to be deletable — a bitmap
            // still selected into a context is not, and the leak accumulates per capture.
            SelectObject(memory, previous);
            if (!copied)
            {
                throw new InvalidOperationException("Windows refused to copy the screen.");
            }

            return _EncodePng(memory, bitmap, bounds.Width, bounds.Height);
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memory != IntPtr.Zero)
            {
                DeleteDC(memory);
            }
        }
    }

    // One monitor as the capture contract describes it, or null when Windows will not say — a display that was
    // unplugged between being enumerated and being asked about is the ordinary way that happens.
    private static DesktopDisplay? _Describe(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return null;
        }

        // A monitor whose DPI cannot be read is not a monitor to leave out — the scale is carried for callers
        // sizing something in its own pixels, and the mapping does not use it. Unscaled is the honest default.
        var scale = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 ? dpiX / UnscaledDpi : 1d;

        return new DesktopDisplay
        {
            Bounds = new CaptureRect(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top),
            Scale = scale,
        };
    }

    // The bitmap's rows read straight into Skia's buffer and encoded. A negative height asks GDI for top-down
    // rows, which is the order Skia expects — the default is bottom-up and would hand back the desktop upside
    // down.
    private static byte[] _EncodePng(IntPtr deviceContext, IntPtr bitmap, int width, int height)
    {
        using var surface = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = BiRgb,
        };

        var rows = GetDIBits(deviceContext, bitmap, 0, (uint)height, surface.GetPixels(), ref header, DibRgbColors);
        if (rows != height)
        {
            throw new InvalidOperationException($"Windows returned {rows} of the screen's {height} rows.");
        }

        using var image = SKImage.FromBitmap(surface);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The captured screen could not be encoded as a PNG.");

        return encoded.ToArray();
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr clip, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int PixelsPerMeterX;
        public int PixelsPerMeterY;
        public uint ColoursUsed;
        public uint ColoursImportant;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("shcore.dll")]
    private static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr deviceContext, IntPtr bitmap, uint startScan, uint scanLines, IntPtr bits, ref BitmapInfoHeader header, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
