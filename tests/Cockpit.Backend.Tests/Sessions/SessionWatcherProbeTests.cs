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
    private static ISessionHandle _Handle(string paneId, string title, bool isTerminal, bool hasReadableTranscript, SessionWakeState wakeState, bool hasPendingPermission = false)
    {
        var handle = Substitute.For<ISessionHandle>();
        handle.PaneId.Returns(paneId);
        handle.Title.Returns(title);
        handle.IsTerminal.Returns(isTerminal);
        handle.HasReadableTranscript.Returns(hasReadableTranscript);
        handle.ReadWakeStateAsync().Returns(wakeState);
        handle.ReadPendingPermissionsAsync().Returns(hasPendingPermission
            ? [new SessionPendingPermission("tool-1", "Write", "{}", DateTimeOffset.UtcNow)]
            : (IReadOnlyList<SessionPendingPermission>)[]);
        handle.HasOutstandingBackgroundShellsAsync().Returns(false);
        return handle;
    }

    [Fact]
    public async Task TheProbe_ReadsAPaneWithARoute_AndSkipsOneWithout()
    {
        var registry = new SessionRegistry();
        var withRoute = _Handle("pane-1", "AC-1380 worker", isTerminal: false, hasReadableTranscript: true, new SessionWakeState(false, SessionStatus.Busy, true));
        withRoute.ReadTranscriptAsync(Arg.Any<int>()).Returns(new SessionTranscriptSlice(
            [new SessionTranscriptEntry("AssistantText", "cutting the branch", null), new SessionTranscriptEntry("ToolUse", "error: the build fell over", null)],
            TotalEntries: 7));
        registry.Register(withRoute);

        var terminal = _Handle("pane-2", "shell", isTerminal: true, hasReadableTranscript: false, new SessionWakeState(false, SessionStatus.Idle, false));
        registry.Register(terminal);

        // AC-294: not a plain terminal, but a provider that records nothing readable (or whose pty is not up yet) —
        // the case `IsTerminal` alone cannot tell apart from the routed pane above.
        var unreadable = _Handle("pane-3", "codex, mid-launch", isTerminal: false, hasReadableTranscript: false, new SessionWakeState(false, SessionStatus.Busy, true));
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

    // AC-1311/AC-1324: an SDK session stopped on a tool's Allow/Deny question is exactly what `needs-attention`
    // exists for, even with no consent banner open and a status that itself does not say NeedsAttention.
    [Fact]
    public async Task TheProbe_NeedsAttention_WhenAToolPermissionIsPendingWithNoOtherSignal()
    {
        var registry = new SessionRegistry();
        var waiting = _Handle("pane-1", "worker", isTerminal: false, hasReadableTranscript: true,
            new SessionWakeState(HasPendingConsent: false, SessionStatus.Busy, CanTakeAPrompt: false), hasPendingPermission: true);
        waiting.ReadTranscriptAsync(Arg.Any<int>()).Returns(SessionTranscriptSlice.Empty);
        registry.Register(waiting);

        var pane = await SessionWatcher.ProbeOf(registry)("pane-1", 0);

        Assert.NotNull(pane);
        Assert.True(pane!.NeedsAttention);
    }
}
