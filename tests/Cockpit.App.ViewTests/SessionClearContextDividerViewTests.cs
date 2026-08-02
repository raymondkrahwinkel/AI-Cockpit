using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-564 criterion 3: after a context clear the transcript stays readable with a clear break at the point of
/// clearing. A render-level check rather than a view-model one — the row exists either way, and what the ticket
/// asks for is that the operator can see it.
/// </summary>
[Collection("avalonia")]
public class SessionClearContextDividerViewTests
{
    private const string Label = "Context cleared — a new conversation starts here";

    [Fact]
    public void TheContextClearedDivider_RendersAsARuleAcrossTheTranscript() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "before the clear"));
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Divider, Label));

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var labels = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text => text.Text == Label && text.IsEffectivelyVisible)
            .ToList();
        // The rule itself: the hairlines either side of the label, which is what makes it read as a break in the
        // conversation rather than as one more line of it.
        var rules = window.GetVisualDescendants()
            .OfType<Border>()
            .Count(border => border is { Height: 1, IsEffectivelyVisible: true });

        window.Close();

        Assert.Single(labels);
        Assert.True(rules >= 2, $"the divider draws a hairline on each side of its label (found {rules})");
    });

    [Fact]
    public void ADividerRow_IsNotAlsoRenderedAsAPlainTranscriptLine() => HeadlessAvalonia.Run(() =>
    {
        var divider = new TranscriptEntryViewModel(TranscriptEntryKind.Divider, Label);

        // Both the reply-column text and the divider bind the same Text, so a divider that still counted as plain
        // text would silently render twice — visible as a doubled label rather than as an error.
        Assert.False(divider.IsPlainText);
        Assert.False(divider.IsPlainNonMarkdown);
        Assert.True(divider.IsDivider);
    });
}
