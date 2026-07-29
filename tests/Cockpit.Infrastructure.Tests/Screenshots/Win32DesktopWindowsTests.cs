using System.Runtime.InteropServices;
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

        Assert.NotEmpty(windows);
        Assert.All(windows, window => Assert.True(window.Title.Length > 0));
        Assert.All(windows, window => Assert.True(window.Bounds.Width > 0 && window.Bounds.Height > 0));
    }

    // A former assertion here checked that no enumerated window was wider or taller than the virtual screen
    // (AC-370). Windows does not guarantee that: a window dragged partly off-screen, or restored onto a display
    // that shrank since it was last positioned, legitimately reports bounds sticking out past the virtual
    // screen. The check was flaky because it depended on whatever happened to be open on the machine running the
    // test, not on a bug in this class. The invariant that actually matters — that a window hanging off the edge
    // is still handled, cropped to the part of it that was captured — is covered deterministically against a fake
    // IDesktopWindows in ScreenshotWindowPickingTests.AWindowHalfOffTheScreen_IsOfferedForThePartThatWasCaptured,
    // which does not depend on the live desktop.
}
