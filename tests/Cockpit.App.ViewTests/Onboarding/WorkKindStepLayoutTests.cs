using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// What a long list does to the answer (AC-511 criterion 7). The wizard shell is 620x480 and cannot be resized, so
/// a work kind that pre-ticks six plugins is exactly the case where the confirm button gets pushed past the bottom
/// edge — the defect <c>PluginConsentDialog</c> already shipped once, on a consent screen, where an answer nobody
/// can reach is the whole failure.
/// </summary>
[Collection("avalonia")]
public class WorkKindStepLayoutTests
{
    [Theory]
    [InlineData("first-run-work-kind")]
    [InlineData("first-run-work-kind-long")]
    public void TheConfirmButton_StaysWithinTheWindow_HoweverManyPluginsAreListed(string scene) => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene(scene);
        try
        {
            window.UpdateLayout();

            var confirm = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => (button.Content as string)?.StartsWith("Install", StringComparison.Ordinal) == true)
                ?? throw new InvalidOperationException($"'{scene}' rendered no Install button to measure");

            var bottom = confirm.TranslatePoint(new Point(0, confirm.Bounds.Height), window)
                ?? throw new InvalidOperationException("the Install button is not in the window's visual tree");

            Assert.True(bottom.Y <= window.Bounds.Height,
                $"'{scene}' puts the Install button's bottom at {bottom.Y:F0}px in a {window.Bounds.Height:F0}px window.");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The list is what gives way, not the confirmation: six rows overflow their viewport and scroll. Without this
    /// the test above would also pass on a step that simply clipped the sixth plugin out of existence.
    /// </summary>
    [Fact]
    public void SixPreTickedPlugins_OverflowTheListAndScroll_RatherThanDisappearing() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("first-run-work-kind-long");
        try
        {
            window.UpdateLayout();

            var list = window.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(viewer => viewer.Name == "PluginList")
                ?? throw new InvalidOperationException("the work-kind step rendered no plugin list to measure");

            Assert.Equal(6, window.GetVisualDescendants().OfType<CheckBox>().Count());
            Assert.True(list.Extent.Height > list.Viewport.Height,
                $"six rows measured {list.Extent.Height:F0}px inside a {list.Viewport.Height:F0}px viewport, so nothing scrolls "
                + "and the rows past the fold are simply gone.");
        }
        finally
        {
            window.Close();
        }
    });
}
