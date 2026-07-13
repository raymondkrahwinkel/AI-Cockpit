using Cockpit.Core.Notifications;
using FluentAssertions;

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
        FinishedNotificationDecision.ShouldNotify(isSelected: true, isWindowActive: true, PresenceState.Present)
            .Should().BeFalse();
    }

    [Fact]
    public void AnotherSessionSelected_Notifies()
    {
        FinishedNotificationDecision.ShouldNotify(isSelected: false, isWindowActive: true, PresenceState.Present)
            .Should().BeTrue();
    }

    [Fact]
    public void WindowInTheBackground_Notifies()
    {
        FinishedNotificationDecision.ShouldNotify(isSelected: true, isWindowActive: false, PresenceState.Present)
            .Should().BeTrue();
    }

    [Fact]
    public void AwayFromThePc_NotifiesEvenForTheSelectedSessionInAFocusedWindow()
    {
        FinishedNotificationDecision.ShouldNotify(isSelected: true, isWindowActive: true, PresenceState.Away)
            .Should().BeTrue();
    }
}
