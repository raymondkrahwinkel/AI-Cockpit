using Avalonia.Controls;
using Avalonia.LogicalTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-151 workspace body renders each phase on a real (headless) visual tree: the scoping card, a refused point
/// with its reason, and the running point with its session embedded — isolated — through the context (AC-122/AC-85).
/// It also guards the two lifecycle traps a run surface has: re-rendering must not reparent the embedded view, and a
/// new point must end the previous run's session rather than orphan it.
/// </summary>
[Collection("avalonia")]
public class AutopilotWorkspaceBodyRenderTests
{
    [Fact]
    public void Body_ShowsScoping_ThenEmbedsTheIsolatedSession_WhenTheRunAdvances()
    {
        var context = _Context(out _, view: "EMBEDDED-SESSION");
        var runs = new AutopilotRunController();
        var body = _Body(context, runs);
        _Show(body);

        runs.BeginScoping(new AutopilotRun("youtrack", "AC-151", "opstart", new Dictionary<string, string>()));
        _Texts(body).Should().Contain(text => text.Contains("Scoping"));

        runs.MarkRunning();

        context.Received(1).EmbedSession(Arg.Is<EmbeddedSessionRequest>(request => request.IsolateInWorktree));
        _Texts(body).Should().Contain("EMBEDDED-SESSION");
    }

    [Fact]
    public void Body_ShowsTheRefusalReason_AndEmbedsNothing_WhenScopingRefuses()
    {
        var context = Substitute.For<IWorkspaceContext>();
        var runs = new AutopilotRunController();
        var body = _Body(context, runs);
        _Show(body);

        runs.BeginScoping(new AutopilotRun("github-issues", "owner/repo#7", "vague", new Dictionary<string, string>()));
        runs.Refuse("no clear acceptance");

        _Texts(body).Should().Contain("no clear acceptance");
        context.DidNotReceive().EmbedSession(Arg.Any<EmbeddedSessionRequest>());
    }

    [Fact]
    public void Body_ReAttachedWhileRunning_DoesNotReparentOrReembed()
    {
        var context = _Context(out _, view: "EMBEDDED-SESSION");
        var runs = new AutopilotRunController();
        var body = _Body(context, runs);
        var window = _Show(body);

        runs.BeginScoping(new AutopilotRun("youtrack", "AC-151", "opstart", new Dictionary<string, string>()));
        runs.MarkRunning();

        // The operator switches tabs away and back: the cockpit re-attaches this same body instance.
        window.Content = null;
        window.Content = body;

        context.Received(1).EmbedSession(Arg.Any<EmbeddedSessionRequest>());
        _Texts(body).Should().Contain("EMBEDDED-SESSION");
    }

    [Fact]
    public void Body_WhenANewPointTakesOver_ClosesThePreviousSession_AndEmbedsAgain()
    {
        var first = _Embedded("SESSION-ONE");
        var second = _Embedded("SESSION-TWO");
        var context = Substitute.For<IWorkspaceContext>();
        context.EmbedSession(Arg.Any<EmbeddedSessionRequest>()).Returns(first, second);
        var runs = new AutopilotRunController();
        var body = _Body(context, runs);
        _Show(body);

        runs.BeginScoping(new AutopilotRun("youtrack", "AC-1", "first", new Dictionary<string, string>()));
        runs.MarkRunning();

        runs.BeginScoping(new AutopilotRun("youtrack", "AC-2", "second", new Dictionary<string, string>()));
        first.Received(1).CloseAsync();

        runs.MarkRunning();
        context.Received(2).EmbedSession(Arg.Any<EmbeddedSessionRequest>());
        _Texts(body).Should().Contain("SESSION-TWO").And.NotContain("SESSION-ONE");
    }

    private static IWorkspaceContext _Context(out IEmbeddedSession embedded, string view)
    {
        embedded = _Embedded(view);
        var context = Substitute.For<IWorkspaceContext>();
        context.EmbedSession(Arg.Any<EmbeddedSessionRequest>()).Returns(embedded);
        return context;
    }

    private static IEmbeddedSession _Embedded(string view)
    {
        var embedded = Substitute.For<IEmbeddedSession>();
        embedded.View.Returns(new TextBlock { Text = view });
        embedded.CloseAsync().Returns(Task.CompletedTask);
        return embedded;
    }

    private static AutopilotWorkspaceBody _Body(IWorkspaceContext context, AutopilotRunController runs) =>
        new(context, new AutopilotSettings(Substitute.For<IPluginStorage>()), runs);

    private static Window _Show(Control body)
    {
        var window = new Window { Content = body };
        window.Show();
        return window;
    }

    private static List<string> _Texts(Control root) =>
        [.. root.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text).OfType<string>()];
}
