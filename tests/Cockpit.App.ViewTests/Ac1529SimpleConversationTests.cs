using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.Composition;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Tests.Shared;
using Material.Icons.Avalonia;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1529: a conversation in the Simple stand — the header's stand in plain words, the assistant without the orange
// line, and the same header over a terminal.
[Collection("avalonia")]
public sealed class Ac1529SimpleConversationTests
{
    // Criterion 1: never an enum name. The busy row with a tool running carries what it is doing after the dot,
    // in the same plain words as its step (AC-1530 criterion 2) rather than the composer band's "Read  ·  invoices.csv".
    [Theory]
    [InlineData(SessionStatus.Idle, false, "Idle")]
    [InlineData(SessionStatus.Busy, false, "Busy")]
    [InlineData(SessionStatus.Busy, true, "Busy · Reading invoices.csv…")]
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
        }

        session.SessionStatus = status;

        Assert.Equal(expected, session.SimpleStatusLabel);
    });

    // Criteria 3 and 4 on the `simple-view` scene (the assistant docked beside a picked agent, a consent bypass on): no
    // orange line in the Simple stand while Panels' window keeps it, and a terminal gets the same header. AC-1533: the
    // assistant's Details name the bypass, a terminal's offer no Start over. AC-1526: at ~220px nothing paints over another.
    [Fact]
    public void TheAssistant_LeavesTheOrangeLineToPanels_AndATerminalGetsTheSameHeader_ThatHoldsInANarrowColumn() => HeadlessAvalonia.Run(() =>
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
            Assert.EndsWith("Some actions you allowed in Settings also run without asking you first.", chat.Details()!.AllowedTo);

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
            header.ProjectName = "Month-end accounts";
            header.Width = 220;
            window.UpdateLayout();
            _AssertNothingPaintsOverAnother(header);
            Assert.False(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeaderPanel").IsEffectivelyVisible);
            var details = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DetailsButton");
            details.Flyout!.ShowAt(details);
            Assert.False(((Control)((Flyout)details.Flyout).Content!).GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DetailsStartOver").IsEffectivelyEnabled);
            details.Flyout.Hide();

            cockpit.SimpleSelectedSession = null;

            Assert.False(cockpit.SimpleStandDocksTheAssistant);
            Assert.True(cockpit.SimpleStandShowsTheAssistantInTheMainColumn);
            Assert.False(terminal.IsPaneVisible);

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

    // AC-1533 criteria 1 and 2: ⋯ → Details say what a conversation may do in plain words, never the mode's own name,
    // and with Cockpit's consent bypass on they add what that lets through — where the orange line used to say it.
    [Theory]
    [InlineData("default", false, "Asks before changing files or running commands.")]
    [InlineData("acceptEdits", false, "Reads and changes files in this folder without asking first; asks before running commands.")]
    [InlineData("plan", false, "Only reads and plans; changes nothing.")]
    [InlineData("bypassPermissions", false, "Does everything without asking first.")]
    [InlineData("dontAsk", false, "(dontAsk)")]
    [InlineData("default", true, "Asks before changing files or running commands. Some actions you allowed in Settings also run without asking you first.")]
    public void TheDetails_SayWhatItMayDo_InPlainWords(string mode, bool consentBypass, string expected) =>
        Assert.Equal(expected, ConversationDetails.Allowed(mode, consentBypass));

    private static void _AssertNothingPaintsOverAnother(Control root)
    {
        var painted = root.GetVisualDescendants().OfType<Control>()
            .Where(control => control is TextBlock or MaterialIcon or Ellipse && control.IsEffectivelyVisible)
            .Select(control => (control, rect: _Painted(control, root)))
            .Where(item => item.rect.Width > 0.5 && item.rect.Height > 0.5)
            .ToList();
        for (var i = 0; i < painted.Count; i++)
        {
            foreach (var other in painted.Skip(i + 1))
            {
                var overlap = painted[i].rect.Intersect(other.rect);
                Assert.True(overlap.Width <= 0.5 || overlap.Height <= 0.5,
                    $"{_Describe(painted[i].control)} paints over {_Describe(other.control)}");
            }
        }
    }

    // An untrimmed TextBlock paints its whole line past a slot that is too small; an ancestor that clips cuts it back.
    private static Rect _Painted(Control control, Control root)
    {
        var width = control is TextBlock { TextTrimming: var trimming } text && trimming == TextTrimming.None
            ? Math.Max(text.Bounds.Width, text.TextLayout.WidthIncludingTrailingWhitespace)
            : control.Bounds.Width;
        var rect = new Rect(control.TranslatePoint(default, root)!.Value, new Size(width, control.Bounds.Height));
        for (var parent = control.GetVisualParent(); parent is not null && parent != root; parent = parent.GetVisualParent())
        {
            if (parent.ClipToBounds)
            {
                rect = rect.Intersect(new Rect(parent.TranslatePoint(default, root)!.Value, parent.Bounds.Size));
            }
        }

        return rect;
    }

    private static string _Describe(Control control) => control is TextBlock text ? $"\"{text.Text}\"" : control.GetType().Name;

    private static Button _Notice(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConsentBypassNotice"
            && button.FindAncestorOfType<AssistantChatView>()?.IsEffectivelyVisible == true);
}
