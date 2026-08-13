using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-755: the diagnostics report is monospace and never wraps, so its longest lines — paths, OS build strings —
/// run past the dialog. The Debug tab's own ScrollViewer only scrolls down, which left that text simply cut off
/// with no way to read it. Measured on the real dialog: a line wider than the panel must be reachable.
/// </summary>
[Collection("avalonia")]
public class DiagnosticsReportScrollTests
{
    [Fact]
    public void ALineWiderThanThePanel_CanBeScrolledTo() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new CockpitViewModel();
        var dialog = new OptionsDialog { DataContext = viewModel };
        dialog.Show();

        var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Header as string == "Debug");
        dialog.UpdateLayout();

        viewModel.Diagnostics.Report = new string('x', 4000);
        dialog.UpdateLayout();

        var report = dialog.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Single(block => block.Text?.StartsWith("xxxx", StringComparison.Ordinal) == true);
        var scroller = report.GetVisualAncestors().OfType<ScrollViewer>().First();

        Assert.True(scroller.Extent.Width > scroller.Viewport.Width, "the report is not wider than what it is shown in, so this proves nothing");

        scroller.Offset = scroller.Offset.WithX(scroller.Extent.Width - scroller.Viewport.Width);
        dialog.UpdateLayout();
        Assert.True(scroller.Offset.X > 0, "the report does not scroll sideways, so its longest lines stay unreadable");

        dialog.Close();
    });
}
