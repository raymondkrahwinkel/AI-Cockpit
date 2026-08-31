using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-638: the assistant's own chat window drew no <see cref="TranscriptEntryKind.Divider"/> row at all, so the
/// hand-over note the host now adds has to be checked where the operator reads it rather than in the view model.
/// </summary>
[Collection("avalonia")]
public class AssistantHandoverDividerViewTests
{
    private const string Label = "Context was full — a new conversation starts here, picked up from a short note";

    [Theory]
    [InlineData(420)] // the window's own width
    [InlineData(340)] // its MinWidth: the narrowest the operator can drag it
    public void TheHandoverDivider_RendersAsARuleAcrossTheTranscript(int width) => HeadlessAvalonia.Run(() =>
    {
        var window = _ChatWindowShowing(_TranscriptEndingInADivider(), width);

        var labels = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text => text.Text == Label && text.IsEffectivelyVisible)
            .ToList();
        // The hairlines are what make it read as a break rather than as one more line of the conversation, and
        // they are the half that silently disappears: the label sits in the Grid's Auto column, which measures its
        // child unconstrained, so a label too long to wrap squeezes both star columns to nothing.
        var rules = labels.SingleOrDefault()?.GetVisualParent()?.GetVisualChildren()
            .OfType<Border>()
            .Where(border => border is { Height: 1, IsEffectivelyVisible: true })
            .Select(border => border.Bounds.Width)
            .ToList() ?? [];

        window.Close();

        Assert.Single(labels);
        Assert.Equal(2, rules.Count);
        Assert.All(rules, ruleWidth => Assert.True(ruleWidth > 0, $"a hairline was squeezed to {ruleWidth}px at {width}px wide"));
    });

    [Fact]
    public void ADividerRow_DrawsNothingElseAroundItsLabel() => HeadlessAvalonia.Run(() =>
    {
        // The divider shares the row with every other kind's template, all bound to the same Text — a divider that
        // still matched one of them would render its label twice rather than fail outright.
        var window = _ChatWindowShowing(_TranscriptEndingInADivider(), width: 420);

        var showingTheLabel = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Count(text => text.Text == Label && text.IsEffectivelyVisible);

        window.Close();

        Assert.Equal(1, showingTheLabel);
    });

    // AC-1261: a clear runs through the same startFresh path as the hand-over above (`_StartAsync:421-428`), so
    // its divider is the same row template rendering different text — one fact proving that text shows is enough,
    // the rendering mechanism itself is already covered above.
    [Fact]
    public void TheClearDivider_RendersTheSameWay_AsTheHandoverDivider() => HeadlessAvalonia.Run(() =>
    {
        const string clearLabel = "Conversation cleared — a new one starts here";
        var window = _ChatWindowShowing(_TranscriptEndingInADivider(clearLabel), width: 420);

        var labels = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text => text.Text == clearLabel && text.IsEffectivelyVisible)
            .ToList();

        window.Close();

        Assert.Single(labels);
    });

    private static SessionViewModel _TranscriptEndingInADivider(string label = Label)
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "before the hand-over"));
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Divider, label));
        return session;
    }

    private static AssistantChatWindow _ChatWindowShowing(SessionViewModel session, int width)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);

        var window = new AssistantChatWindow
        {
            Width = width,
            DataContext = new AssistantChatViewModel(
                host,
                Substitute.For<IAssistantSettingsStore>(),
                Substitute.For<IVoicePlaybackQueue>()),
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
