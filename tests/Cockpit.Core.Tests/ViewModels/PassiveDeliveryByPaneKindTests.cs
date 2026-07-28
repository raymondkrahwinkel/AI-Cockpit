using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Which panes carry mail on their own turns (AC-394). The honest half of this ticket: a session the host composes
/// turns for can do it, and a CLI running inside a pty cannot — there the host writes bytes and the program on the
/// other side decides what a turn is, and injected text must not carry an Enter nobody pressed.
/// <para>
/// Asserted per pane rather than trusted to a type check somewhere, because this answer travels: it is what
/// <c>list_agents</c> reports and what a sender uses to decide whether silence from a pane means anything.
/// </para>
/// </summary>
public class PassiveDeliveryByPaneKindTests
{
    [Fact]
    public void ASessionPaneWiredForDelivery_SaysSo()
    {
        var vm = new SessionViewModel(
            Substitute.For<Cockpit.Core.Abstractions.Sessions.ISessionManager>(),
            turnInboxDelivery: Substitute.For<IAgentTurnInboxDelivery>());

        Assert.True(vm.DeliversInboxAtTurnStart);
    }

    /// <summary>
    /// The same class, built without the seam, answers false — and it must, because it is the same class either way
    /// and only one of the two will ever carry a message. A hard-coded <c>true</c> passes a test that asserts on the
    /// kind; it takes an instance built the other way to show the claim is about wiring.
    /// </summary>
    [Fact]
    public void ASessionPaneWithoutTheSeam_DoesNotClaimToDeliver()
    {
        Assert.False(new SessionViewModel().DeliversInboxAtTurnStart);
    }

    [Fact]
    public void ATerminalPane_DoesNot_AndSaysSoRatherThanLettingItBeAssumed()
    {
        Assert.False(TtyViewModel.DesignTerminal().DeliversInboxAtTurnStart);
    }
}
