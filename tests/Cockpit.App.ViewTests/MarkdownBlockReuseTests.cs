using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.LogicalTree;
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

        view.Markdown = first;
        await Task.Delay(150);
        var rendered = Assert.IsType<StackPanel>(view.Content);
        var built = Assert.IsAssignableFrom<Control>(Assert.Single(rendered.Children));

        view.Markdown = then;
        await Task.Delay(150);
        return (built, rendered);
    }

    [Fact]
    public async Task AGrowingCodeFence_KeepsTheBorderItAlreadyBuilt()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (built, rendered) = await _Stream(
                "```csharp\nvar x = 1;\n",
                "```csharp\nvar x = 1;\nvar y = 2;\n");

            Assert.Same(built, Assert.Single(rendered.Children));
            var code = built.GetLogicalDescendants().OfType<SelectableTextBlock>().First();
            Assert.Contains("var y = 2;", code.Text);
        });
    }

    [Fact]
    public async Task AGrowingTable_KeepsTheGridAndGainsARow()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            const string start = "| Repo | Status |\n|------|--------|\n| one | work |\n";
            var (built, rendered) = await _Stream(start, start + "| two | official |\n");

            Assert.Same(built, Assert.Single(rendered.Children));
            var grid = Assert.IsType<Grid>(Assert.IsAssignableFrom<Border>(built).Child);

            // Header row plus both body rows, in the grid that was there before the second row arrived.
            Assert.Equal(3, grid.RowDefinitions.Count);
        });
    }

    [Fact]
    public async Task AGrowingList_KeepsTheRowsBeforeTheNewItem()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (built, rendered) = await _Stream("- first\n", "- first\n- second\n");

            Assert.Same(built, Assert.Single(rendered.Children));
            var panel = Assert.IsType<StackPanel>(built);
            Assert.Equal(2, panel.Children.Count);
        });
    }

    /// <summary>
    /// The copy button costs more to build than the rest of a fenced block put together, and a transcript row is
    /// rebuilt every time the virtualising panel realises it again — so it waits for a pointer, which anyone who
    /// is going to click it has to move onto the block first anyway.
    /// </summary>
    [Fact]
    public async Task ACodeFence_GetsItsCopyButtonOnTheFirstHover()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var view = new MarkdownView();
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();

            view.Markdown = "```csharp\nvar x = 1;\n```\n";
            await Task.Delay(150);

            var border = Assert.IsAssignableFrom<Border>(
                Assert.Single(Assert.IsType<StackPanel>(view.Content).Children));

            Assert.Empty(border.GetLogicalDescendants().OfType<Button>());

            border.RaiseEvent(new PointerEventArgs(
                InputElement.PointerEnteredEvent, border, new Pointer(0, PointerType.Mouse, true),
                border, default, 0, default, default));

            Assert.Single(border.GetLogicalDescendants().OfType<Button>());
        });
    }

    /// <summary>
    /// A paragraph the stream is still writing: the block keeps its text control, so the runs are replaced rather
    /// than a fresh SelectableTextBlock built per repaint.
    /// </summary>
    [Fact]
    public async Task AGrowingParagraph_KeepsItsTextControl()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (built, rendered) = await _Stream("the reply begins", "the reply begins and carries on");

            Assert.Same(built, Assert.Single(rendered.Children));

            // The text lives in the runs, not in Text: a paragraph is built from inlines so that bold, code and
            // links can be styled per run.
            var text = Assert.IsAssignableFrom<SelectableTextBlock>(built).Inlines;
            Assert.NotNull(text);
            Assert.Contains("carries on", string.Concat(text!.OfType<Run>().Select(r => r.Text)));
        });
    }
}
