using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// AC-1316: a reply table wider than the pane wraps its cells instead of running off the right edge. Measured on
// the rendered session view, since the same MarkdownView serves the assistant chat through TranscriptRowView.
[Collection("avalonia")]
public class Ac1316TableFitsPaneTests
{
    [Fact]
    public void ATableWiderThanThePane_StaysInsideItsRow() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("session-table", width: 520, height: 520);
        try
        {
            window.UpdateLayout();

            var markdown = window.GetVisualDescendants().OfType<MarkdownView>().Single(v => v.Markdown?.Contains("| # | Guard |", StringComparison.Ordinal) == true);
            var table = markdown.GetVisualDescendants().OfType<Grid>().Single(g => g.Children.OfType<Border>().Count() > 4);
            // The grid itself is arranged to its slot; it is the cells that used to overflow it (AC-1316).
            var right = table.Children.Max(cell => cell.TranslatePoint(new Point(cell.Bounds.Width, 0), markdown)!.Value.X);

            Assert.True(right <= markdown.Bounds.Width + 0.5,
                $"the table's right edge ({right:0}) falls past its row's width ({markdown.Bounds.Width:0})");
        }
        finally
        {
            window.Close();
        }
    });
}
