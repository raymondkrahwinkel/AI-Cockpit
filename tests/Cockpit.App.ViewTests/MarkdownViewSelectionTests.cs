using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-679/AC-680: text in a <see cref="MarkdownView"/> reply looked unselectable, but only because it was
/// invisible — its private <c>InlineTextBlock</c> subclassed <see cref="SelectableTextBlock"/> without
/// overriding <c>StyleKeyOverride</c>, so it never picked up FluentTheme's SelectionBrush/cursor/Copy-menu.
/// </summary>
[Collection("avalonia")]
public sealed class MarkdownViewSelectionTests
{
    [Fact]
    public void AParagraphsTextBlock_GetsTheSameSelectionBrushAPlainSelectableTextBlockGets() => HeadlessAvalonia.Run(() =>
    {
        var view = new MarkdownView { Markdown = "Some prose." };
        var plain = new SelectableTextBlock();
        var window = new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { view, plain } } };
        window.Show();
        try
        {
            var paragraph = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single();

            Assert.NotNull(plain.SelectionBrush);
            Assert.Equal(plain.SelectionBrush, paragraph.SelectionBrush);
        }
        finally
        {
            window.Close();
        }
    });

    // The real thing end to end: an actual SessionView, its own real SessionViewModel, one assistant-markdown
    // transcript row — same DataTemplate, same styles, same code-behind wiring as production.
    [Fact]
    public void DraggingAcrossAnAssistantReply_InARealSessionView_SelectsText() => HeadlessAvalonia.Run(() =>
    {
        var vm = new SessionViewModel();
        vm.Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.AssistantText, "Hello world this is a paragraph of selectable text."));

        var view = new SessionView { DataContext = vm };
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();
        try
        {
            // Let the deferred focus/layout posts SessionView.OnAttachedToVisualTree queues actually run.
            Dispatcher.UIThread.RunJobs();

            var markdown = view.GetVisualDescendants().OfType<MarkdownView>()
                .Single(m => m.Markdown == "Hello world this is a paragraph of selectable text.");
            var text = markdown.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
            var from = text.TranslatePoint(new Point(2, text.Bounds.Height / 2), window)!.Value;
            var to = text.TranslatePoint(new Point(text.Bounds.Width - 2, text.Bounds.Height / 2), window)!.Value;

            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left);

            Assert.False(string.IsNullOrEmpty(text.SelectedText));
        }
        finally
        {
            window.Close();
        }
    });

    // The known pitfall the ticket flagged: block reuse. Every delta re-sets Markdown, which MarkdownView
    // reconciles in place on the very paragraph being dragged across — the selected instance must survive it.
    [Fact]
    public void DraggingAcrossAReplyThatIsStillStreaming_SelectsText() => HeadlessAvalonia.Run(() =>
    {
        var vm = new SessionViewModel();
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Hello world this is");
        vm.Transcript.Add(entry);

        var view = new SessionView { DataContext = vm };
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var markdown = view.GetVisualDescendants().OfType<MarkdownView>()
                .Single(m => m.Markdown == "Hello world this is");
            var text = markdown.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
            var from = text.TranslatePoint(new Point(2, text.Bounds.Height / 2), window)!.Value;

            window.MouseDown(from, MouseButton.Left);

            // Deltas arrive mid-drag, same as a live stream — each one runs MarkdownView._Render() in place.
            entry.AppendText(" a paragraph");
            Dispatcher.UIThread.RunJobs();
            var to = text.TranslatePoint(new Point(text.Bounds.Width - 2, text.Bounds.Height / 2), window)!.Value;
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            entry.AppendText(" of selectable text.");
            Dispatcher.UIThread.RunJobs();

            window.MouseUp(to, MouseButton.Left);

            Assert.False(string.IsNullOrEmpty(text.SelectedText));
        }
        finally
        {
            window.Close();
        }
    });
}
