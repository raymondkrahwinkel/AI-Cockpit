namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public sealed class ScreenshotterTests
{
    [Fact]
    public void Run_WithAnExtensionlessOutputPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => Screenshotter.Run("first-run-work-kind-long"));
    }

    // AC-1235: the misspelling that renders. A name nobody registered used to build the main window, so the run
    // wrote a real PNG of the wrong screen and reported success — the reason a scene renamed under an agent's feet
    // is worse than one that fails.
    [Fact]
    public void BuildScene_WithAnUnknownName_ThrowsAndListsTheKnownOnes()
    {
        var thrown = Assert.Throws<ArgumentException>(() => Screenshotter.BuildScene("session-consent-developer"));

        Assert.Contains("session-consent-plain-developer", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScene_WithNoSceneAtAll_StillBuildsTheMainWindow()
    {
        Assert.IsType<Views.MainWindow>(HeadlessAvalonia.Run(() => Screenshotter.BuildScene(scene: null)));
    }
}
