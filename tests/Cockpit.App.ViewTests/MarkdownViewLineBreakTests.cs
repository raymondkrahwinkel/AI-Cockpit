using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// AC-936: a chat message typed with Shift+Enter must keep each line on its own visual line (Slack/Discord
// style) instead of CommonMark's single-space join. PreserveLineBreaks is the opt-in that only the chat
// bubble turns on — everything else (assistant replies, DelegatedTasksDialog, FilePreviewWindow) still runs
// the default path exercised by the second test here.
[Collection("avalonia")]
public sealed class MarkdownViewLineBreakTests
{
    [Fact]
    public void PreserveLineBreaks_KeepsEachShiftEnterLineOnItsOwnLine() => HeadlessAvalonia.Run(() =>
    {
        var view = new MarkdownView
        {
            PreserveLineBreaks = true,
            Markdown = "plan 1: ok\nplan 2: fine\nplan 3: great",
        };
        var window = new Window { Content = view, Width = 400, Height = 200 };
        window.Show();
        try
        {
            var text = Assert.IsAssignableFrom<SelectableTextBlock>(
                Assert.Single(Assert.IsType<StackPanel>(view.Content).Children));

            Assert.Equal(2, text.Inlines!.OfType<LineBreak>().Count());
            Assert.Equal(
                new[] { "plan 1: ok", "plan 2: fine", "plan 3: great" },
                text.Inlines!.OfType<Run>().Select(r => r.Text));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void DefaultBehaviour_StillJoinsWrappedLinesLikeARealMarkdownDocument() => HeadlessAvalonia.Run(() =>
    {
        var view = new MarkdownView { Markdown = "plan 1: ok\nplan 2: fine" };
        var window = new Window { Content = view, Width = 400, Height = 200 };
        window.Show();
        try
        {
            var text = Assert.IsAssignableFrom<SelectableTextBlock>(
                Assert.Single(Assert.IsType<StackPanel>(view.Content).Children));

            Assert.Empty(text.Inlines!.OfType<LineBreak>());
            Assert.Equal("plan 1: ok plan 2: fine", string.Concat(text.Inlines!.OfType<Run>().Select(r => r.Text)));
        }
        finally
        {
            window.Close();
        }
    });
}
