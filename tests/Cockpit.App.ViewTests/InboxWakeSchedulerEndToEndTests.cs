using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-656, end to end and with no model anywhere in it: a real <see cref="AgentMessageInbox"/>, a real
/// <see cref="WorkspaceAgentGateway"/> over a real <see cref="CockpitViewModel"/>, and a real
/// <see cref="InboxWakeScheduler"/> tick — proving mail sitting in an idle pane's inbox gets that pane an actual
/// turn without a sender ever marking anything urgent and without the recipient ever calling
/// <c>set_wake_optin</c>. The mirror image of <c>AssistantAgentAddressTests</c>' piggyback end-to-end test: that one
/// proves mail arrives on the recipient's own next tool call; this one proves mail on its own starts that call's
/// turn in the first place.
/// </summary>
[Collection("avalonia")]
public sealed class InboxWakeSchedulerEndToEndTests
{
    [Fact]
    public async Task MailWaitingInAnIdlePanesInbox_GetsThatPaneATurn_WithNoUrgentFlagAndNoOptIn()
    {
        var inbox = new AgentMessageInbox();

        var (cockpit, target, sent) = Dispatcher.UIThread.Invoke(() =>
        {
            var vm = new CockpitViewModel();
            var to = new TtyViewModel { SessionStatus = SessionStatus.Idle };
            var captured = new List<string>();
            to.PromptSink = text => captured.Add(text);
            to.MarkHostedTuiReady();
            vm.Sessions.Add(to);
            return (vm, to, captured);
        });

        var gateway = new WorkspaceAgentGateway(cockpit, NullLogger<WorkspaceAgentGateway>.Instance);
        var scheduler = new InboxWakeScheduler(inbox, gateway) { Panes = () => [target.PaneId] };

        // A peer's own delivery — no notify tool, no urgent=true, no set_wake_optin call for the recipient anywhere
        // in this test. The inbox alone is what the scheduler acts on.
        var delivery = inbox.Deliver("pane-sender", target.PaneId, "heads-up", "the branch moved to main");
        Assert.Equal(AgentMessageDeliveryOutcome.Delivered, delivery.Outcome);
        Assert.Empty(sent);

        await scheduler.RunOnceAsync();

        var turn = Assert.Single(sent);
        Assert.Contains("<cockpit-agent-wake", turn, StringComparison.Ordinal);
        Assert.Contains("pane-sender", turn, StringComparison.Ordinal);
        // The truthful statement for this trigger: no claim that the recipient opted into anything, because it
        // never did.
        Assert.DoesNotContain("opted in", turn, StringComparison.Ordinal);
        Assert.Contains(AgentWakeTurnNotice.WaitingMailStatement, turn, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same real components, but nothing waiting anywhere — the tick must not touch the gateway at all, which is
    /// the "costs nothing" half of the same story proven against production wiring rather than substitutes.
    /// </summary>
    [Fact]
    public async Task ATickOverARealCockpitWithEmptyInboxes_StartsNoTurns()
    {
        var inbox = new AgentMessageInbox();

        var (cockpit, target, sent) = Dispatcher.UIThread.Invoke(() =>
        {
            var vm = new CockpitViewModel();
            var to = new TtyViewModel { SessionStatus = SessionStatus.Idle };
            var captured = new List<string>();
            to.PromptSink = text => captured.Add(text);
            vm.Sessions.Add(to);
            return (vm, to, captured);
        });

        var gateway = new WorkspaceAgentGateway(cockpit, NullLogger<WorkspaceAgentGateway>.Instance);
        var scheduler = new InboxWakeScheduler(inbox, gateway) { Panes = () => [target.PaneId] };

        await scheduler.RunOnceAsync();

        Assert.Empty(sent);
    }
}
