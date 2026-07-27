using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>A desktop that reports exactly the windows a test hands it, front to back — or none, standing in for Wayland (AC-330).</summary>
internal sealed class StubDesktopWindows : IDesktopWindows
{
    public static StubDesktopWindows None => new() { IsSupported = false };

    public bool IsSupported { get; init; } = true;

    public IReadOnlyList<DesktopWindow> Windows { get; init; } = [];

    public IReadOnlyList<DesktopWindow> Enumerate() => Windows;
}
