using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Which kinds of pane carry mail on their own turns (AC-394). The honest half of this ticket: a session the host
/// composes turns for can do it, and a CLI running inside a pty cannot — there the host writes bytes and the program
/// on the other side decides what a turn is, and injected text must not carry an Enter nobody pressed.
/// <para>
/// Asserted per pane kind rather than trusted to a type check somewhere, because this answer travels: it is what
/// <c>list_agents</c> reports and what a sender uses to decide whether silence from a pane means anything.
/// </para>
/// </summary>
public class PassiveDeliveryByPaneKindTests
{
    [Fact]
    public void ASessionPane_CarriesMailOnItsOwnTurns()
    {
        Assert.True(new SessionViewModel().DeliversInboxAtTurnStart);
    }

    [Fact]
    public void ATerminalPane_DoesNot_AndSaysSoRatherThanLettingItBeAssumed()
    {
        Assert.False(TtyViewModel.DesignTerminal().DeliversInboxAtTurnStart);
    }
}
