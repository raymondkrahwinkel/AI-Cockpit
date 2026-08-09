using Cockpit.App.ViewModels;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-444 #2: the rail's ordering key, on the pane the same predicate <c>SessionWatcher</c> probes for
/// OS-notification purposes — one definition of "needs eyes on it", not two that can drift apart.
/// </summary>
[Collection("avalonia")]
public class SessionPanelRailOrderingTests
{
    [Theory]
    [InlineData(SessionStatus.Idle, false)]
    [InlineData(SessionStatus.Busy, false)]
    [InlineData(SessionStatus.WorkingBackground, false)]
    [InlineData(SessionStatus.WaitingForInput, true)]
    [InlineData(SessionStatus.NeedsAttention, true)]
    [InlineData(SessionStatus.Done, false)]
    public void RequestsAttention_FollowsSessionStatus(SessionStatus status, bool expected) => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { SessionStatus = status };

        Assert.Equal(expected, session.RequestsAttention);
    });

    [Fact]
    public void RequestsAttention_AlsoTrueWithAPendingConsentPrompt_EvenWhileIdle() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { SessionStatus = SessionStatus.Idle };
        var request = new ConsentRequest(
            "Run a command", "ls -la", new ConsentSource("pane-1", null, "Test"), "test.run", ConsentRisk.LowRisk);

        session.PendingConsent = new ConsentPromptViewModel(new ConsentPrompt(Guid.NewGuid(), request, CanRemember: false), new _NoOpBroker());

        Assert.True(session.RequestsAttention);
    });

    // `ConsentPromptViewModel` only reaches its broker from the Approve/Deny commands, neither of which this
    // test exercises — a no-op stands in rather than the real `ConsentService` and everything it wires up.
    private sealed class _NoOpBroker : IConsentBroker
    {
        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConsentDecision.Denied);

        public event EventHandler<ConsentPrompt>? PromptOpened { add { } remove { } }

        public event EventHandler<Guid>? PromptClosed { add { } remove { } }

        public void Respond(Guid promptId, ConsentOutcome outcome, bool remember)
        {
        }
    }

    [Fact]
    public void RailSortKey_PutsEveryAttentionNeedingSessionBeforeEveryQuietOne_RegardlessOfSidebarIndex() => HeadlessAvalonia.Run(() =>
    {
        var quiet = new SessionViewModel { SessionStatus = SessionStatus.Idle, SidebarIndex = 0 };
        var attention = new SessionViewModel { SessionStatus = SessionStatus.NeedsAttention, SidebarIndex = 99 };

        Assert.True(attention.RailSortKey < quiet.RailSortKey,
            "a session needing attention must sort first no matter how far down the sidebar it sits");
    });

    [Fact]
    public void RailSortKey_WithinTheSameAttentionGroup_FollowsSidebarIndex() => HeadlessAvalonia.Run(() =>
    {
        var earlier = new SessionViewModel { SessionStatus = SessionStatus.Idle, SidebarIndex = 1 };
        var later = new SessionViewModel { SessionStatus = SessionStatus.Idle, SidebarIndex = 2 };

        Assert.True(earlier.RailSortKey < later.RailSortKey);
    });
}
