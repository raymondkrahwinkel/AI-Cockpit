using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;

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
    [InlineData(SessionStatus.NeedsAttention, true)]
    [InlineData(SessionStatus.Done, false)]
    // AC-1311 criterion 3: Failed falls in the same needs-eyes-on-it class as NeedsAttention — a crashed session
    // must sort to the top of the rail exactly like one waiting on a permission does.
    [InlineData(SessionStatus.Failed, true)]
    public void RequestsAttention_FollowsSessionStatus(SessionStatus status, bool expected) => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { SessionStatus = status };

        Assert.Equal(expected, session.RequestsAttention);
    });

    [Fact]
    public void RailSortKey_PutsEveryAttentionNeedingSessionBeforeEveryQuietOne_RegardlessOfSidebarIndex() => HeadlessAvalonia.Run(() =>
    {
        var quiet = new SessionViewModel { SessionStatus = SessionStatus.Idle, SidebarIndex = 0 };
        var attention = new SessionViewModel { SessionStatus = SessionStatus.NeedsAttention, SidebarIndex = 99 };

        Assert.True(attention.RailSortKey < quiet.RailSortKey,
            "a session needing attention must sort first no matter how far down the sidebar it sits");
    });
}
