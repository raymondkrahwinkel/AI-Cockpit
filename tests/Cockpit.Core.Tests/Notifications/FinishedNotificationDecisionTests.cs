using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// Whether a finished session announces itself: the one case that stays silent is the session you are actually
/// watching — selected, in a focused window, with you at the PC. Anything else means the answer would go
/// unnoticed, which is what the notification is for.
/// </summary>
public class FinishedNotificationDecisionTests
{
    // The one silent case is the session you are actually watching. Another session selected, a window in the
    // background, or you away from the PC each mean the answer would go unnoticed — which is what the
    // notification is for.
    [Theory]
    [InlineData(true, true, PresenceState.Present, false)]
    [InlineData(false, true, PresenceState.Present, true)]
    [InlineData(true, false, PresenceState.Present, true)]
    [InlineData(true, true, PresenceState.Away, true)]
    public void OnlyTheSessionYouAreWatching_StaysSilent(bool isSelected, bool isWindowActive, PresenceState presence, bool expected)
    {
        Assert.Equal(expected, FinishedNotificationDecision.ShouldNotify(isSelected, isWindowActive, presence));
    }
}
