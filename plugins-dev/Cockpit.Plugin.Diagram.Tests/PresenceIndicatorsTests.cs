using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugin.Diagram.Collab;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-847: the presence pips, live-action line and change counter — reusing ActivityStripTests' fakes (now
// internal, not private, for exactly this) rather than writing a second stand-in for the same two interfaces.
[Collection("avalonia")]
public class PresenceIndicatorsTests
{
    private static Window _Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Ellipse _AgentPip(Control content) => content.GetVisualDescendants().OfType<Ellipse>().First();

    private static Ellipse _OperatorPip(Control content) => content.GetVisualDescendants().OfType<Ellipse>().Skip(1).First();

    private static List<string?> _Texts(Control content) =>
        content.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    private static DiagramHistoryEntry DiagramEntry(string id, string origin, string summary, bool reverted = false) =>
        new(id, origin, DiagramHandEditKind.AddNode, "N1", summary, DateTime.Now, reverted);

    [Fact]
    public void NoCoupling_TheWholeControlIsHidden()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        var journal = new DiagramActivityJournal(registry);
        var presence = new PresenceIndicators("surface-1", journal, journal);
        var window = _Show(presence);

        Assert.False(presence.IsVisible);

        window.Close();
    }

    [Fact]
    public void CoupledWithZeroCapability_IsVisible_AndBothPipsAreIdle()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        var journal = new DiagramActivityJournal(registry);
        var presence = new PresenceIndicators("surface-1", journal, journal);
        var window = _Show(presence);

        registry.SetCoupling("surface-1", new DiagramCoupling("pane-a", CanRead: false, CanEdit: false));
        Dispatcher.UIThread.RunJobs();

        Assert.True(presence.IsVisible);
        Assert.Equal("Agent coupled, no permissions", ToolTip.GetTip(_AgentPip(presence)));
        Assert.Contains(_Texts(presence), text => text is not null && text.Contains("coupled, nothing asked yet", StringComparison.Ordinal));

        window.Close();
    }

    [Fact]
    public void CoupledWithRead_ShowsTheReadingPipAndLine()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        var journal = new DiagramActivityJournal(registry);
        var presence = new PresenceIndicators("surface-1", journal, journal);
        var window = _Show(presence);

        registry.SetCoupling("surface-1", new DiagramCoupling("pane-a", CanRead: true, CanEdit: false));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Agent reading along", ToolTip.GetTip(_AgentPip(presence)));
        Assert.Contains(_Texts(presence), text => text is not null && text.Contains("reading along", StringComparison.Ordinal));

        window.Close();
    }

    [Fact]
    public void AFreshNonOperatorEdit_SwitchesTheAgentPipAndLineToWriting()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        var journal = new DiagramActivityJournal(registry);
        var presence = new PresenceIndicators("surface-1", journal, journal);
        var window = _Show(presence);
        presence.SetSession("pane-a", "Werksessie");

        registry.SetCoupling("surface-1", new DiagramCoupling("pane-a", CanRead: true, CanEdit: true));
        registry.Seed("surface-1", DiagramEntry("e1", "pane-a", "added node N1 \"Foo\""));
        registry.Raise("surface-1");
        Dispatcher.UIThread.RunJobs();

        // The pulse sets the "writing" flag synchronously before it ever awaits, so this is observable without
        // waiting out the 3s window (see DiagramCollabWindowTests for the fade itself, on the diagram's own cursor).
        Assert.Equal("Agent editing", ToolTip.GetTip(_AgentPip(presence)));
        Assert.Contains(_Texts(presence), text => text is not null && text.Contains("Werksessie: added node N1 \"Foo\"", StringComparison.Ordinal));

        window.Close();
    }

    [Fact]
    public void OperatorHold_SwitchesTheOperatorPipToWriting()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        var journal = new DiagramActivityJournal(registry);
        var presence = new PresenceIndicators("surface-1", journal, journal);
        var window = _Show(presence);
        registry.SetCoupling("surface-1", new DiagramCoupling("pane-a", CanRead: true, CanEdit: true));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("You are present", ToolTip.GetTip(_OperatorPip(presence)));

        presence.SetOperatorWriting(true);
        Assert.Equal("You are editing", ToolTip.GetTip(_OperatorPip(presence)));

        window.Close();
    }

    [Fact]
    public void ChangeCounter_CountsEntriesSinceConstruction_ButARevertNeverInflatesIt()
    {
        var registry = new ActivityStripTests.FakeDiagramRegistry();
        // Already there before the window opened — not part of "since I've been watching".
        registry.Seed("surface-1", DiagramEntry("e0", "operator", "renamed A to \"Begin\""));
        var journal = new DiagramActivityJournal(registry);
        var presence = new PresenceIndicators("surface-1", journal, journal);
        var window = _Show(presence);
        registry.SetCoupling("surface-1", new DiagramCoupling("pane-a", CanRead: true, CanEdit: true));

        registry.Seed("surface-1", DiagramEntry("e1", "pane-a", "added node N1 \"Foo\""));
        registry.Raise("surface-1");
        registry.Seed("surface-1", DiagramEntry("e2", "operator", "removed node N2"));
        registry.Raise("surface-1");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(_Texts(presence), text => text == "2 changes");

        // Revert mutates the existing entry in place (FakeDiagramRegistry.Revert), it never appends — the count
        // must stay exactly where it was.
        registry.Revert("surface-1", "e1");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(_Texts(presence), text => text == "2 changes");

        window.Close();
    }

    [Fact]
    public void Whiteboard_NoCoupling_IsAlsoHidden_ThenVisibleOnceCoupled()
    {
        var registry = new ActivityStripTests.FakeWhiteboardRegistry();
        var journal = new WhiteboardActivityJournal(registry);
        var presence = new PresenceIndicators("board-1", journal, journal);
        var window = _Show(presence);

        Assert.False(presence.IsVisible);

        registry.SetCoupling("board-1", new WhiteboardCoupling("pane-a", CanRead: true));
        Dispatcher.UIThread.RunJobs();
        Assert.True(presence.IsVisible);

        window.Close();
    }
}
