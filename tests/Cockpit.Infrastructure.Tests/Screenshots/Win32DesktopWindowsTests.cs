using System.Runtime.InteropServices;
using FluentAssertions;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// The Windows window list against the real desktop (AC-330). What the picker does with it is arithmetic and is
/// tested elsewhere; this is the part where a wrong DWM attribute number or a missed filter shows up — and it
/// does not fail loudly. A list of invisible helper windows enumerates perfectly well.
/// </summary>
/// <remarks>Runs only on Windows, so CI (Linux) passes over it — evidence from the machine it ran on, not a gate.</remarks>
public class Win32DesktopWindowsTests
{
    [Fact]
    public void TheDesktopsWindowsAreReported_WithTitlesAndArea()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var windows = new Win32DesktopWindows().Enumerate();

        windows.Should().NotBeEmpty("a machine running this has something on screen");
        windows.Should().OnlyContain(window => window.Title.Length > 0, "a rectangle nobody can name is not one to offer");
        windows.Should().OnlyContain(window => window.Bounds.Width > 0 && window.Bounds.Height > 0);
    }

    /// <summary>
    /// Nothing is reported larger than the screen it sits on. A weak check, and knowingly so: it does not tell
    /// the extended frame bounds from <c>GetWindowRect</c>, whose invisible resize border is a few pixels rather
    /// than anything that would show up here. Distinguishing the two needs a window whose real edges are known,
    /// which the desktop cannot supply — so which attribute is asked for stays a reading of the documentation,
    /// and a maximised window with a band of the wallpaper around it is what a human would notice in [g].
    /// </summary>
    [Fact]
    public void NoWindowIsReportedWiderThanTheVirtualScreen()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var screen = new Win32ScreenReader().ReadLayout().VirtualBounds;

        foreach (var window in new Win32DesktopWindows().Enumerate())
        {
            window.Bounds.Width.Should().BeLessThanOrEqualTo(screen.Width);
            window.Bounds.Height.Should().BeLessThanOrEqualTo(screen.Height);
        }
    }
}
