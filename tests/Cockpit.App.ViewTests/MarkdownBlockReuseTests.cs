using Avalonia.Controls;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A table or a fence is a single block however long it grows, so block-level reuse alone still rebuilt its whole
/// grid — or its border, scroller and copy button — on every repaint of a streaming reply. Measured over a 4 KB
/// reply that was 231 MB for a table and 188 MB for a fence, against 48 MB for the same length of prose, which
/// splits into small blocks of which only the last is touched.
/// </summary>
[Collection("avalonia")]
public sealed class MarkdownBlockReuseTests
{
    /// <summary>Renders <paramref name="first"/>, hands back the control it built, then appends to it.</summary>
    private static async Task<(Control Built, StackPanel Rendered)> _Stream(string first, string then)
    {
        var view = new MarkdownView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();

        // The first set on a fresh view renders inline: OnPropertyChanged calls _Render synchronously because no
        // rebuild timer is running yet (nothing to coalesce). No wait needed.
        view.Markdown = first;
        var rendered = Assert.IsType<StackPanel>(view.Content);
        var built = Assert.IsAssignableFrom<Control>(Assert.Single(rendered.Children));

        // The first render started the 33 ms rebuild timer (MarkdownView.OnPropertyChanged), so this second set
        // only flags a pending rebuild and returns — the actual _Render runs off that timer's next tick. Await
        // the real completion instead of a fixed sleep guessing the tick landed.
        view.Markdown = then;
        await view.WaitForPendingRenderAsync();
        return (built, rendered);
    }

    [Fact]
    public async Task AGrowingTable_KeepsTheGridAndGainsARow()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            const string start = "| Repo | Status |\n|------|--------|\n| one | work |\n";
            var (built, rendered) = await _Stream(start, start + "| two | official |\n");

            Assert.Same(built, Assert.Single(rendered.Children));
            var grid = Assert.IsAssignableFrom<Grid>(Assert.IsAssignableFrom<Border>(built).Child);

            // Header row plus both body rows, in the grid that was there before the second row arrived.
            Assert.Equal(3, grid.RowDefinitions.Count);
        });
    }
}
