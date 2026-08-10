using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>AC-680: the same MarkdownView selection defect AC-679 fixed, pinned here for the Assistant pop-out window.</summary>
[Collection("avalonia")]
public sealed class AssistantChatWindowSelectionTests
{
    [Fact]
    public void AnAssistantReplysTextBlock_GetsTheSameSelectionBrushAPlainSelectableTextBlockGets() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Some prose."));

        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);

        var window = new AssistantChatWindow
        {
            Width = 420,
            Height = 560,
            DataContext = new AssistantChatViewModel(
                host,
                Substitute.For<IAssistantSettingsStore>(),
                Substitute.For<IVoicePlaybackQueue>()),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // A separate window rather than a sibling in AssistantChatWindow's own content: it only needs the same
        // FluentTheme lookup a shown window gives any control, not to share a parent with the paragraph.
        var plainWindow = new Window { Content = new SelectableTextBlock() };
        plainWindow.Show();
        try
        {
            var plain = (SelectableTextBlock)plainWindow.Content!;
            var paragraph = window.GetVisualDescendants().OfType<MarkdownView>()
                .Single(m => m.Markdown == "Some prose.")
                .GetVisualDescendants().OfType<SelectableTextBlock>().Single();

            Assert.NotNull(plain.SelectionBrush);
            Assert.Equal(plain.SelectionBrush, paragraph.SelectionBrush);
        }
        finally
        {
            plainWindow.Close();
            window.Close();
        }
    });
}
