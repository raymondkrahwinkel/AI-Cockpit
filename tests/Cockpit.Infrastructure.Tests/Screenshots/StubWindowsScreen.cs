using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>A Windows desktop that is whatever a test says it is, and records what was asked of it — GDI without a screen behind it.</summary>
internal sealed class StubWindowsScreen : IWindowsScreenReader
{
    public required CaptureRect VirtualBounds { get; init; }

    public required IReadOnlyList<DesktopDisplay> Displays { get; init; }

    public bool IsPerMonitorDpiAware { get; init; } = true;

    /// <summary>What the virtual screen becomes once the capture has been taken — a display unplugged mid-capture.</summary>
    public CaptureRect? VirtualBoundsAfterCapture { get; init; }

    /// <summary>The rectangle the capture asked to have blitted, or null when it never got that far.</summary>
    public CaptureRect? Requested { get; private set; }

    public WindowsScreenLayout ReadLayout() =>
        new()
        {
            VirtualBounds = Requested is not null && VirtualBoundsAfterCapture is { } changed ? changed : VirtualBounds,
            Displays = Displays,
        };

    public byte[] CapturePng(CaptureRect bounds)
    {
        Requested = bounds;
        return [0x89, 0x50, 0x4E, 0x47];
    }
}
