using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Xunit.Abstractions;

namespace Cockpit.Plugin.YouTrack.Tests;

// AC-521 Iron Law #9: the placeholder help added above the template and branch-pattern fields is more explanation
// text stacked above an input, and that is a real risk in a settings panel — enough of it pushes the field the
// operator actually needs to reach further down or off the visible area. This renders the real control at the
// settings dialog's own size (640x560, see `CockpitViewModel.OpenPluginSettingsAsync`) and measures where the
// template box actually lands, rather than eyeballing it.
[Collection("avalonia")]
public class SettingsControlTemplateFitTests
{
    private readonly ITestOutputHelper _out;

    public SettingsControlTemplateFitTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TemplateBox_StaysFullyReachableWithinTheSettingsDialogsOwnScrollViewer() => HeadlessAvalonia.Run(() =>
    {
        const string marker = "AC-521-FIT-MARKER";
        var settings = new YouTrackSettings(new InMemoryPluginStorage()) { Template = marker };
        var view = new YouTrackSettingsControl(settings);

        // The window a real plugin-settings dialog opens at (CockpitViewModel.OpenPluginSettingsAsync: 640x560).
        // The view itself is the whole dialog body here — no chrome/footer subtracted — so this is the most
        // generous case the real dialog gets; the real one has less room, not more.
        const double width = 640;
        const double height = 560;

        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        window.UpdateLayout();

        var target = new RenderTargetBitmap(new PixelSize((int)width, (int)height));
        target.Render(window);

        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().OrderByDescending(sv => sv.Bounds.Height).First();
        var templateBox = view.GetVisualDescendants().OfType<TextBox>().Single(box => box.Text == marker);

        // Scroll fully to the end — the worst case for "did we push it out of reach".
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        window.UpdateLayout();

        var boxTop = templateBox.TranslatePoint(default, window) ?? default;
        var boxBottom = templateBox.TranslatePoint(new Point(0, templateBox.Bounds.Height), window) ?? default;
        var viewportBottom = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window) ?? default;

        _out.WriteLine($"window={width}x{height}  scrollExtent={scroll.Extent.Height:0.#}  scrollViewport={scroll.Viewport.Height:0.#}  " +
                       $"templateBox.Height={templateBox.Bounds.Height:0.#}  templateBox.Top(afterScroll)={boxTop.Y:0.#}  " +
                       $"templateBox.Bottom(afterScroll)={boxBottom.Y:0.#}  viewportBottom={viewportBottom.Y:0.#}");

        window.Close();

        Assert.True(templateBox.Bounds.Height >= 140, $"the template box collapsed to {templateBox.Bounds.Height:0.#}px, below its own 140px MinHeight");
        Assert.True(boxBottom.Y <= viewportBottom.Y + 0.5,
            $"the template box's bottom ({boxBottom.Y:0.#}px) sits below the dialog's own scroll viewport ({viewportBottom.Y:0.#}px) even fully scrolled — the operator cannot reach it");
    });
}
