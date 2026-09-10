using Avalonia.VisualTree;
using Cockpit.App.Controls;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-488 criterion 1: a cockpit with no projects shows the starting-point gallery rather than saying it has none.
/// Rendered through the screenshot harness's own scenes rather than a view model assembled here, so the claim is
/// made against the markup an operator sees and not against a tree only this test knows how to build.
/// </summary>
[Collection("avalonia")]
public sealed class Ac488StartingPointsTests
{
    /// <summary>
    /// Both directions in one test, because the claim is a swap and not a presence: a gallery that shows whenever
    /// the workspace is open would pass the first half on its own while burying the projects it is meant to
    /// replace. The two surfaces are asserted the same way for the same reason — one gallery, two empty screens.
    /// </summary>
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
