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

            // AC-737: the user row now builds a MarkdownView too (just hidden for a non-user row), so
            // matching on markdown text alone is no longer unique — narrow to the one actually shown.
            var markdown = view.GetVisualDescendants().OfType<MarkdownView>()
                .Single(m => m.Markdown == "Hello world this is a paragraph of selectable text." && m.IsEffectivelyVisible);
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
}
