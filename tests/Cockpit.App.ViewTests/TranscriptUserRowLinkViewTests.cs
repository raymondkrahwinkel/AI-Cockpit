using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// AC-737: the user's own chat bubble rendered through a plain SelectableTextBlock, which has no
// link detection or click handling at all — a pasted URL just sat there as inert text. Asserts
// the row now goes through the same MarkdownView the assistant reply already used for that.
[Collection("avalonia")]
public class TranscriptUserRowLinkViewTests
{
    [Fact]
    public void AUserMessageWithALink_RendersThroughMarkdownView() => HeadlessAvalonia.Run(() =>
    {
        const string text = "check [the docs](https://example.com/docs)";
        var session = new SessionViewModel();
        session.Transcript.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, text));

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The assistant-reply Grid (and its MarkdownView) is always built, just IsVisible="False" for a
        // user row — GetVisualDescendants finds it either way, so effective visibility is what distinguishes
        // "rendered" from "present but hidden".
        var rendersViaMarkdownView = window.GetVisualDescendants()
            .OfType<MarkdownView>()
            .Any(view => view.Markdown == text && view.IsEffectivelyVisible);

        window.Close();

        Assert.True(rendersViaMarkdownView, "a user message should render through MarkdownView so its links are detected and clickable");
    });
}
