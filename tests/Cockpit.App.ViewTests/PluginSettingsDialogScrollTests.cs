using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using FluentAssertions;
using Xunit.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The plugin settings dialog wraps a plugin's view in a host-owned <see cref="ScrollViewer"/> above a docked
/// Save/Close footer (<c>PluginDialogHost.ShowSettingsDialogAsync</c>). A plugin whose view is taller than the
/// dialog must be able to scroll the whole view clear of that footer — the last control has to reach a position
/// above the footer's top edge, not stay pinned underneath it.
/// <para>
/// This reproduces the footer-overlap bug seen with the Kubernetes plugin: the fix is that a plugin view must NOT
/// add its own ScrollViewer, because a ScrollViewer nested in the host's ScrollViewer is measured with unbounded
/// height and never clips — its tail renders under the footer and cannot be scrolled into view.
/// </para>
/// </summary>
[Collection("avalonia")]
public class PluginSettingsDialogScrollTests
{
    private readonly ITestOutputHelper _out;

    public PluginSettingsDialogScrollTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void PaddingOnTheScrollViewer_StillClipsTheLastRow() => HeadlessAvalonia.Run(() =>
    {
        var (window, marker, footer) = Build(nestOwnScrollViewer: false, insetInsideContent: false);

        var gap = MeasureClearance(window, marker, footer, "single SV, padding on the ScrollViewer");

        window.Close();

        // Diagnostic: padding set on the ScrollViewer itself is left out of the scroll extent, so the last row's
        // tail cannot be scrolled clear of the footer.
        gap.Should().BeLessThan(0, "ScrollViewer padding is not counted in the scroll extent");
    });

    [Fact]
    public void InsetInsideTheContent_ScrollsTallContentClearOfTheFooter() => HeadlessAvalonia.Run(() =>
    {
        var (window, marker, footer) = Build(nestOwnScrollViewer: false, insetInsideContent: true);

        var gap = MeasureClearance(window, marker, footer, "single SV, inset inside the content (fix)");

        window.Close();

        // When scrolled to the end, the last control's bottom must sit at or above the footer's top: fully visible.
        gap.Should().BeGreaterThanOrEqualTo(0,
            "with the inset inside the scrolled content the extent includes it, so the last control scrolls clear");
    });

    [Fact]
    public void ANestedPluginScrollViewer_LeavesContentTrappedUnderTheFooter() => HeadlessAvalonia.Run(() =>
    {
        var (window, marker, footer) = Build(nestOwnScrollViewer: true, insetInsideContent: false);

        var gap = MeasureClearance(window, marker, footer, "nested ScrollViewer (the bug)");

        window.Close();

        // Diagnostic only: proves the nested-ScrollViewer structure is what traps content under the footer.
        // A negative gap means the marker renders below the footer's top edge and cannot be scrolled up.
        gap.Should().BeLessThan(0,
            "a ScrollViewer nested in the host ScrollViewer is measured unbounded and cannot scroll its tail clear");
    });

    private double MeasureClearance(Window window, Control marker, Control footer, string label)
    {
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
            .OrderByDescending(sv => sv.Bounds.Height).First();
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        window.UpdateLayout();

        var markerBottom = marker.TranslatePoint(new Point(0, marker.Bounds.Height), window) ?? default;
        var footerTop = footer.TranslatePoint(new Point(0, 0), window) ?? default;
        var gap = footerTop.Y - markerBottom.Y;

        _out.WriteLine($"[{label}] markerBottom={markerBottom.Y:0.#}  footerTop={footerTop.Y:0.#}  gap={gap:0.#}  " +
                       $"viewport={scroll.Viewport.Height:0.#} extent={scroll.Extent.Height:0.#} offset={scroll.Offset.Y:0.#}");
        return gap;
    }

    // Reproduces exactly what PluginDialogHost.ShowSettingsDialogAsync builds: a docked footer, the view in a
    // host ScrollViewer, all under the shared CockpitWindowChrome. The "view" stands in for a plugin settings
    // control tall enough to overflow the dialog; nestOwnScrollViewer models a plugin wrapping its own content.
    private static (Window window, Control marker, Control footer) Build(bool nestOwnScrollViewer, bool insetInsideContent)
    {
        var stack = new StackPanel { Spacing = 8 };
        for (int i = 0; i < 40; i++)
        {
            stack.Children.Add(new CheckBox { Content = $"Setting row {i}" });
        }

        var marker = new CheckBox { Content = "Let sessions use the MCP tools" };
        stack.Children.Add(marker);

        Control view = nestOwnScrollViewer
            ? new UserControl { Content = new ScrollViewer { Content = stack } }
            : new UserControl { Content = stack };

        // The fix: put the dialog inset inside the scrolled content (a padded Border) so the ScrollViewer's extent
        // includes it. Padding on the ScrollViewer itself (the else branch, the current host behaviour) is left out
        // of the extent and clips the tail under the footer.
        var scroll = insetInsideContent
            ? new ScrollViewer { Content = new Border { Padding = new Thickness(14, 12), Child = view } }
            : new ScrollViewer { Content = view, Padding = new Thickness(14, 12) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { new Button { Content = "Close" }, new Button { Content = "Save" } },
        };
        var footer = new Border
        {
            Padding = new Thickness(14, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = buttons,
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(footer);
        root.Children.Add(scroll);

        // Mirror the host's toast-overlay z-stack wrapper without depending on the overlay control itself.
        var body = new Panel { Children = { root, new Border { IsHitTestVisible = false } } };

        var window = new Window { Width = 720, Height = 480, Content = body };
        CockpitWindowChrome.Apply(window, "Settings");
        window.Show();
        window.UpdateLayout();
        return (window, marker, footer);
    }
}
