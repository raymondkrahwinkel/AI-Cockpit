using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Sessions;

/// <summary>
/// AC-1380 acceptance 3: <see cref="SessionWatcher.ProbeOf"/> now takes an <see cref="ISessionRegistry"/> instead of
/// a <c>CockpitViewModel</c>, and reads a pane's transcript through <see cref="ISessionHandle.ReadTranscriptAsync"/>
/// alone — the one route AC-294 already made an SDK and a TTY session answer alike. There is no branch here on
/// which kind of pane it is; a pane with a readable route is read, one without is not — <c>HasReadableTranscript</c>
/// is what tells a plain terminal and a provider with no reader apart from a session that simply wrote nothing yet.
/// </summary>
public class SessionWatcherProbeTests
{
    [Fact]
    public async Task TheProbe_ReadsAPaneWithARoute_AndSkipsOneWithout()
    {
        var registry = new SessionRegistry();
        var withRoute = Substitute.For<ISessionHandle>();
        withRoute.PaneId.Returns("pane-1");
        withRoute.Title.Returns("AC-1380 worker");
        withRoute.IsTerminal.Returns(false);
        withRoute.HasReadableTranscript.Returns(true);
        withRoute.SessionStatus.Returns(SessionStatus.Busy);
        withRoute.ReadTranscriptAsync(Arg.Any<int>()).Returns(new SessionTranscriptSlice(
            [new SessionTranscriptEntry("AssistantText", "cutting the branch", null), new SessionTranscriptEntry("ToolUse", "error: the build fell over", null)],
            TotalEntries: 7));
        registry.Register(withRoute);

        var terminal = Substitute.For<ISessionHandle>();
        terminal.PaneId.Returns("pane-2");
        terminal.Title.Returns("shell");
        terminal.IsTerminal.Returns(true);
        terminal.SessionStatus.Returns(SessionStatus.Idle);
        registry.Register(terminal);

        // AC-294: not a plain terminal, but a provider that records nothing readable (or whose pty is not up yet) —
        // the case `IsTerminal` alone cannot tell apart from the routed pane above.
        var unreadable = Substitute.For<ISessionHandle>();
        unreadable.PaneId.Returns("pane-3");
        unreadable.Title.Returns("codex, mid-launch");
        unreadable.IsTerminal.Returns(false);
        unreadable.HasReadableTranscript.Returns(false);
        unreadable.SessionStatus.Returns(SessionStatus.Busy);
        registry.Register(unreadable);

        var probe = SessionWatcher.ProbeOf(registry);
        var routed = await probe("pane-1", 6);
        var plain = await probe("pane-2", 0);
        var unrouted = await probe("pane-3", 0);

        Assert.NotNull(routed);
        Assert.True(routed!.HasTranscript);
        Assert.Equal(7, routed.TranscriptRows);
        Assert.Equal(["error: the build fell over"], routed.NewRows);

        Assert.NotNull(plain);
        Assert.False(plain!.HasTranscript);
        Assert.Equal(0, plain.TranscriptRows);

        Assert.NotNull(unrouted);
        Assert.False(unrouted!.HasTranscript);
        Assert.Equal(0, unrouted.TranscriptRows);
    }
}
