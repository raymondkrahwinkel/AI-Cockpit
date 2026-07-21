using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-151/AC-152 workspace body renders each phase on a real (headless) visual tree: the scoping card, a refused
/// point with its reason, and the running point with its isolated session embedded (AC-122/AC-85), confirmed with the
/// operator and briefed only on approval. It also guards the run-surface lifecycle: re-rendering must not reparent the
/// embedded view, and a new point must end the previous run's session rather than orphan it.
/// </summary>
[Collection("avalonia")]
public class AutopilotWorkspaceBodyRenderTests
{
    [Fact]
    public async Task Body_ShowsScoping_ThenEmbedsTheIsolatedSession_AndBriefsItOnApproval()
    {
        var host = _ApprovingHost();
        var embedded = _Embedded("EMBEDDED-SESSION");
        var context = _Context(embedded);
        var runs = new AutopilotRunController();
        var body = _Body(host, context, runs);
        _Show(body);

        runs.BeginScoping(new AutopilotRun("youtrack", "AC-152", "opstart", new Dictionary<string, string>()));
        _Texts(body).Should().Contain(text => text.Contains("Scoping"));

        runs.MarkRunning();
        _Pump();

        context.Received(1).EmbedSession(Arg.Is<EmbeddedSessionRequest>(request =>
            request.IsolateInWorktree && request.PermissionMode == AutopilotSettings.DefaultAutonomyMode));
        await host.Received(1).SendToSessionAsync("pane-EMBEDDED-SESSION", Arg.Is<string>(text => text.Contains("merge-ready")));
        _Texts(body).Should().Contain("EMBEDDED-SESSION");
    }

    [Fact]
    public async Task Body_WhenTheOperatorDeclines_ClosesTheSession_ParksTheRun_AndBriefsNothing()
    {
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>()).Returns(ConsentDecision.Denied);
        var embedded = _Embedded("EMBEDDED-SESSION");
        var context = _Context(embedded);
        var runs = new AutopilotRunController();
        var body = _Body(host, context, runs);
        _Show(body);

        runs.BeginScoping(new AutopilotRun("youtrack", "AC-152", "opstart", new Dictionary<string, string>()));
        runs.MarkRunning();
        _Pump();

        embedded.Received(1).CloseAsync();
        runs.Phase.Should().Be(AutopilotRunPhase.Refused);
        await host.DidNotReceive().SendToSessionAsync(Arg.Any<string>(), Arg.Any<string>());
        _Texts(body).Should().Contain(text => text.Contains("declined"));
    }

    [Fact]
    public void Body_ShowsTheRefusalReason_AndEmbedsNothing_WhenScopingRefuses()
    {
        var context = Substitute.For<IWorkspaceContext>();
        var runs = new AutopilotRunController();
        var body = _Body(_ApprovingHost(), context, runs);
        _Show(body);

        runs.BeginScoping(new AutopilotRun("github-issues", "owner/repo#7", "vague", new Dictionary<string, string>()));
        runs.Refuse("no clear acceptance");

        _Texts(body).Should().Contain("no clear acceptance");
        context.DidNotReceive().EmbedSession(Arg.Any<EmbeddedSessionRequest>());
    }

    [Fact]
    public void Body_ReAttachedWhileRunning_DoesNotReparentOrReembed()
    {
        var embedded = _Embedded("EMBEDDED-SESSION");
        var context = _Context(embedded);
        var runs = new AutopilotRunController();
        var body = _Body(_ApprovingHost(), context, runs);
        var window = _Show(body);

        runs.BeginScoping(new AutopilotRun("youtrack", "AC-152", "opstart", new Dictionary<string, string>()));
        runs.MarkRunning();
        _Pump();

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
        var body = _Body(_ApprovingHost(), context, runs);
        _Show(body);

        runs.BeginScoping(new AutopilotRun("youtrack", "AC-1", "first", new Dictionary<string, string>()));
        runs.MarkRunning();
        _Pump();

        runs.BeginScoping(new AutopilotRun("youtrack", "AC-2", "second", new Dictionary<string, string>()));
        first.Received(1).CloseAsync();

        runs.MarkRunning();
        _Pump();
        context.Received(2).EmbedSession(Arg.Any<EmbeddedSessionRequest>());
        _Texts(body).Should().Contain("SESSION-TWO").And.NotContain("SESSION-ONE");
    }

    private static ICockpitHost _ApprovingHost()
    {
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>()).Returns(new ConsentDecision(ConsentOutcome.Approved));
        return host;
    }

    private static IWorkspaceContext _Context(IEmbeddedSession embedded)
    {
        var context = Substitute.For<IWorkspaceContext>();
        context.EmbedSession(Arg.Any<EmbeddedSessionRequest>()).Returns(embedded);
        return context;
    }

    private static IEmbeddedSession _Embedded(string view)
    {
        var embedded = Substitute.For<IEmbeddedSession>();
        embedded.View.Returns(new TextBlock { Text = view });
        embedded.PaneId.Returns("pane-" + view);
        embedded.CloseAsync().Returns(Task.CompletedTask);
        return embedded;
    }

    private static AutopilotWorkspaceBody _Body(ICockpitHost host, IWorkspaceContext context, AutopilotRunController runs) =>
        new(host, context, new AutopilotSettings(Substitute.For<IPluginStorage>()), runs);

    private static Window _Show(Control body)
    {
        var window = new Window { Content = body };
        window.Show();
        return window;
    }

    // Run the posted confirm-and-brief job (the body defers it off the render), so the consent decision has landed.
    private static void _Pump() => Dispatcher.UIThread.RunJobs();

    private static List<string> _Texts(Control root) =>
        [.. root.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text).OfType<string>()];
}
