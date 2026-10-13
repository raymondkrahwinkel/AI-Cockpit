using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Composition;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Tests.Shared;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1529: a conversation in the Simple stand — the header's stand in plain words, the assistant without the orange
// line, and the same header over a terminal.
[Collection("avalonia")]
public sealed class Ac1529SimpleConversationTests
{
    // Criterion 1: never an enum name. The busy row with a tool running carries what it is doing after the dot.
    [Theory]
    [InlineData(SessionStatus.Idle, false, "Idle")]
    [InlineData(SessionStatus.Busy, false, "Busy")]
    [InlineData(SessionStatus.Busy, true, "Busy · ")]
    [InlineData(SessionStatus.WorkingBackground, false, "Busy")]
    [InlineData(SessionStatus.NeedsAttention, false, "Waiting for you")]
    [InlineData(SessionStatus.Done, false, "Done")]
    [InlineData(SessionStatus.Failed, false, "Stopped with an error")]
    public void TheStand_ReadsInPlainWords(SessionStatus status, bool toolRunning, string expected) => HeadlessAvalonia.Run(() =>
    {
        var session = TestSessions.Pane(Substitute.For<ISessionManager>());
        if (toolRunning)
        {
            session.IsBusy = true;
            session.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "Read", InputJson = "{\"file_path\":\"invoices.csv\"}" });
            Assert.NotEmpty(session.ActiveToolActivityLabel);
            expected += session.ActiveToolActivityLabel;
        }

        session.SessionStatus = status;

        Assert.Equal(expected, session.SimpleStatusLabel);
    });

    // Criterion 3, on the `simple-view` scene (the assistant docked beside a picked agent, with a consent bypass on):
    // no orange line in the Simple stand, docked or in its column — and back in Panels' own window it stands again.
    // Takes over Ac1303's `WithTheAssistantDocked_EachColumnHasItsOwnComposer`: the two composers are checked here.
    [Fact]
    public void TheAssistant_LeavesTheOrangeLineToPanels() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("simple-view");
        var loose = new Window { Width = 420, Height = 520 };
        try
        {
            var cockpit = (CockpitViewModel)window.DataContext!;
            var chat = cockpit.AssistantChat!;
            chat.ConsentBypassActive = true;
            window.UpdateLayout();

            var boxes = window.GetVisualDescendants().OfType<TextBox>().Where(box => box.Name == "InputBox").ToList();
            var agentBox = Assert.Single(boxes, box => box.DataContext is SessionViewModel);
            var assistantBox = Assert.Single(boxes, box => box.DataContext is AssistantChatViewModel);
            agentBox.Text = "restart the kind cluster";
            Assert.Equal("restart the kind cluster", ((SessionViewModel)agentBox.DataContext!).InputText);
            Assert.True(string.IsNullOrEmpty(((AssistantChatViewModel)assistantBox.DataContext!).InputText));
            Assert.False(_Notice(window).IsEffectivelyVisible);

            cockpit.SimpleSelectedSession = cockpit.AssistantRootSession;
            window.UpdateLayout();
            Assert.True(chat.IsSimpleViewHost);
            Assert.False(_Notice(window).IsEffectivelyVisible);

            cockpit.ShowPanelsViewCommand.Execute(null);
            loose.Content = new AssistantChatView { DataContext = chat };
            loose.Show();
            loose.UpdateLayout();
            Assert.True(_Notice(loose).IsEffectivelyVisible);
        }
        finally
        {
            loose.Close();
            window.Close();
        }
    });

    // Criterion 4: a terminal picked in the rail gets the same header (title, labels, stand) with its terminal under
    // it. Takes over Ac1303's `PickingAnAgent_PutsItInTheMainColumn_AndTheAssistantInTheDock`, both ways round.
    [Fact]
    public void ATerminal_GetsTheSameHeader_OverItsTerminal() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("simple-view");
        try
        {
            var cockpit = (CockpitViewModel)window.DataContext!;
            var terminal = new TtyViewModel { Title = "month-end run", ActiveProfileLabel = "personal", ProviderBadge = "CLAUDE", SessionStatus = SessionStatus.Busy };
            cockpit.Sessions.Add(terminal);

            cockpit.SimpleSelectedSession = terminal;
            window.UpdateLayout();

            Assert.True(cockpit.SimpleStandDocksTheAssistant);
            Assert.False(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
            var header = window.GetVisualDescendants().OfType<SimpleConversationHeader>().Single(h => h.IsEffectivelyVisible);
            var view = window.GetVisualDescendants().OfType<TtyView>().Single(v => v.IsEffectivelyVisible);
            Assert.Same(terminal, header.DataContext);
            var shown = header.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Equal(["month-end run", "CLAUDE", "personal", "Busy"], shown);
            Assert.True(header.TranslatePoint(default, window)!.Value.Y < view.TranslatePoint(default, window)!.Value.Y);
            Assert.False(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeaderPanel").IsEffectivelyVisible);

            cockpit.SimpleSelectedSession = null;

            Assert.False(cockpit.SimpleStandDocksTheAssistant);
            Assert.True(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
            Assert.False(terminal.IsPaneVisible);
        }
        finally
        {
            window.Close();
        }
    });

    private static Button _Notice(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConsentBypassNotice"
            && button.FindAncestorOfType<AssistantChatView>()?.IsEffectivelyVisible == true);
}
