using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// Whether a finished session announces itself: the one case that stays silent is the session you are actually
/// watching — selected, in a focused window, with you at the PC. Anything else means the answer would go
/// unnoticed, which is what the notification is for.
/// </summary>
public class FinishedNotificationDecisionTests
{
    [Fact]
    public void WatchingThatSession_StaysSilent()
    {
        Assert.False(FinishedNotificationDecision.ShouldNotify(isSelected: true, isWindowActive: true, PresenceState.Present));
    }

    [Fact]
    public void AnotherSessionSelected_Notifies()
    {
        Assert.True(FinishedNotificationDecision.ShouldNotify(isSelected: false, isWindowActive: true, PresenceState.Present));
    }

    [Fact]
    public void WindowInTheBackground_Notifies()
    {
        Assert.True(FinishedNotificationDecision.ShouldNotify(isSelected: true, isWindowActive: false, PresenceState.Present));
    }

    [Fact]
    public void AwayFromThePc_NotifiesEvenForTheSelectedSessionInAFocusedWindow()
    {
        Assert.True(FinishedNotificationDecision.ShouldNotify(isSelected: true, isWindowActive: true, PresenceState.Away));
    }
}
