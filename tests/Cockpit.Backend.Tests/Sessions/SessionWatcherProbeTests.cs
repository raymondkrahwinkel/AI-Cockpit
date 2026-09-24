using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Sessions;

/// <summary>
/// AC-1380 acceptance 3: <see cref="SessionWatcher.ProbeOf"/> now takes an <see cref="ISessionRegistry"/> instead of
/// a <c>CockpitViewModel</c>, and reads a pane's transcript through <see cref="ISessionHandle.ReadTranscriptAsync"/>
/// alone — the one route AC-294 already made an SDK and a TTY session answer alike. There is no branch here on
/// which kind of pane it is; a pane with a route is read, a plain terminal is not.
/// </summary>
public class SessionWatcherProbeTests
{
    [Fact]
    public async Task TheProbe_ReadsAPaneWithARouteAndSkipsAPlainTerminal()
    {
        var registry = new SessionRegistry();
        var withRoute = Substitute.For<ISessionHandle>();
        withRoute.PaneId.Returns("pane-1");
        withRoute.Title.Returns("AC-1380 worker");
        withRoute.IsTerminal.Returns(false);
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

        var probe = SessionWatcher.ProbeOf(registry);
        var routed = await probe("pane-1", 6);
        var plain = await probe("pane-2", 0);

        Assert.NotNull(routed);
        Assert.True(routed!.HasTranscript);
        Assert.Equal(7, routed.TranscriptRows);
        Assert.Equal(["error: the build fell over"], routed.NewRows);

        Assert.NotNull(plain);
        Assert.False(plain!.HasTranscript);
        Assert.Equal(0, plain.TranscriptRows);
    }
}
