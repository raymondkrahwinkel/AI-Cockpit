using Avalonia.VisualTree;
using Cockpit.App.Controls;

namespace Cockpit.App.ViewTests;

// AC-488: rendered through the screenshot harness's own scenes, so the claim holds against the markup an operator sees.
[Collection("avalonia")]
public sealed class Ac488StartingPointsTests
{
    // Both directions in one test — the claim is a swap, and an always-on gallery would pass the first half on its own.
    [Theory]
    [InlineData("projects-workspace-empty", "projects-workspace-cards")]
    [InlineData("simple-view-start-screen-empty", "simple-view-start-screen")]
    public void TheGallery_StandsWhereThereAreNoProjects_AndStandsDownWhereThereAre(string empty, string filled)
    {
        Assert.True(_DrawsTheGallery(empty), $"{empty} draws the starting points");
        Assert.False(_DrawsTheGallery(filled), $"{filled} leaves them out");
    }

    private static bool _DrawsTheGallery(string scene) => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene(scene);
        try
        {
            window.UpdateLayout();
            // Effectively visible, not merely present: `IsVisible` leaves the control attached to the tree, so
            // asking whether it exists would answer yes on every scene.
            return window.GetVisualDescendants().OfType<StartingPointsView>().Any(gallery => gallery.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });
}
