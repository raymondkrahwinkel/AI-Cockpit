using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// AC-936: a chat message typed with Shift+Enter must keep each line on its own visual line (Slack/Discord
// style) instead of CommonMark's single-space join. PreserveLineBreaks is the opt-in that only the chat
// bubble turns on — everything else (assistant replies, DelegatedTasksDialog, FilePreviewWindow) still runs
// the default path, checked second here.
[Collection("avalonia")]
public sealed class MarkdownViewLineBreakTests
{
    [Fact]
    public void PreserveLineBreaks_KeepsEachShiftEnterLine_WhileTheDefaultStillJoinsThem() => HeadlessAvalonia.Run(() =>
    {
        var preserved = _Render(new MarkdownView { PreserveLineBreaks = true, Markdown = "plan 1: ok\nplan 2: fine\nplan 3: great" });

        Assert.Equal(2, preserved.OfType<LineBreak>().Count());
        Assert.Equal(new[] { "plan 1: ok", "plan 2: fine", "plan 3: great" }, preserved.OfType<Run>().Select(r => r.Text));

        var joined = _Render(new MarkdownView { Markdown = "plan 1: ok\nplan 2: fine" });

        Assert.Empty(joined.OfType<LineBreak>());
        Assert.Equal("plan 1: ok plan 2: fine", string.Concat(joined.OfType<Run>().Select(r => r.Text)));
    });

    private static List<Inline> _Render(MarkdownView view)
    {
        var window = new Window { Content = view, Width = 400, Height = 200 };
        window.Show();
        try
        {
            var text = Assert.IsAssignableFrom<SelectableTextBlock>(
                Assert.Single(Assert.IsType<StackPanel>(view.Content).Children));
            return [.. text.Inlines!];
        }
        finally
        {
            window.Close();
        }
    }
}
