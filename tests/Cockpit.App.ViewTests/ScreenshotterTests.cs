namespace Cockpit.App.ViewTests;

public sealed class ScreenshotterTests
{
    [Fact]
    public void Run_WithAnExtensionlessOutputPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => Screenshotter.Run("first-run-work-kind-long"));
    }
}
