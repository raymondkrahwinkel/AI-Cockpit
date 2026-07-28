using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The wake gate (AC-395), against real panes rather than records built by hand: which live states let a peer's
/// urgent message start a turn, and which refuse it.
/// <para>
/// This is where the refusals that matter are decided, so this is where they are proven. The tool layer can only
/// know what a snapshot said a moment ago; whether the recipient is mid-turn, has a question open in front of its
/// operator, or is still on the caller's desk is true or false at the instant of waking and nowhere else.
/// </para>
/// </summary>
[Collection("avalonia")]
public class WorkspaceAgentGatewayWakeTests
{
    /// <summary>
    /// A pane that both takes prompts and carries mail on its turns. No shipping pane kind is both — a terminal
    /// pane takes a prompt and has no turn to hang delivery on, and a session pane has the turn but needs a live
    /// runtime to take anything. Without one of these the gateway could pass a hard-coded <c>false</c> into the
    /// notice and every test would still be green, while a woken agent was told to go and read an inbox whose
    /// contents were already in front of it.
    /// </summary>
    private sealed class DeliveringTerminal : TtyViewModel
    {
        public override bool DeliversInboxAtTurnStart => true;
    }

    private static (CockpitViewModel Cockpit, TtyViewModel Sender, TtyViewModel Target, List<string> Sent) _Desk(
        SessionStatus targetStatus = SessionStatus.Done)
    {
        return Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var sender = new TtyViewModel();
            var target = new TtyViewModel { SessionStatus = targetStatus };

            // The pty sink is both what makes the pane able to take a prompt and the only place a wake becomes
            // visible — a wake that fires writes here, and one that is refused leaves it empty.
            var sent = new List<string>();
            target.PromptSink = text => sent.Add(text);

            cockpit.Sessions.Add(sender);
            cockpit.Sessions.Add(target);
            return (cockpit, sender, target, sent);
        });
    }

    private static WorkspaceAgentGateway _Gateway(CockpitViewModel cockpit) =>
        new(cockpit, NullLogger<WorkspaceAgentGateway>.Instance);

    [Fact]
    public async Task Wake_OnAPaneStandingStill_StartsATurnCarryingTheLabelledNotice()
    {
        var (cockpit, sender, target, sent) = _Desk();

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        Assert.Equal(AgentWakeOutcome.Woken, outcome);
        var turn = Assert.Single(sent);
        Assert.Contains("<cockpit-agent-wake", turn, StringComparison.Ordinal);
        // The sending pane, not merely "some agent": a recipient that cannot tell who caused its turn cannot weigh
        // what it is being told, and cannot answer.
        Assert.Contains(sender.PaneId, turn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wake_TellsTheRecipientWhereTheMessageIs_FromThePanesOwnDeliveryAnswer()
    {
        var (cockpit, sender, plainTerminal, plainSent) = _Desk();

        var (deliveringCockpit, deliveringSender, delivering, deliveringSent) = Dispatcher.UIThread.Invoke(() =>
        {
            var vm = new CockpitViewModel();
            var from = new TtyViewModel();
            var to = new DeliveringTerminal { SessionStatus = SessionStatus.Done };
            var captured = new List<string>();
            to.PromptSink = text => captured.Add(text);
            vm.Sessions.Add(from);
            vm.Sessions.Add(to);
            return (vm, from, to, captured);
        });

        _ = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, plainTerminal.PaneId, "branch");
        _ = await _Gateway(deliveringCockpit).TryWakeAsync(deliveringSender.PaneId, delivering.PaneId, "branch");

        // Two panes, two different sentences, from one line of gateway code reading each pane's own answer. Asserted
        // as a pair because either sentence alone is satisfied by a constant.
        Assert.Contains("read_inbox", Assert.Single(plainSent), StringComparison.Ordinal);
        Assert.DoesNotContain("read_inbox", Assert.Single(deliveringSent), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SessionStatus.Busy)]
    [InlineData(SessionStatus.WorkingBackground)]
    public async Task Wake_OnAPaneThatIsWorking_IsRefusedAndSendsNothing(SessionStatus status)
    {
        // WorkingBackground looks quiet from outside and is not: a sub-agent is still running, and a turn dropped on
        // top of it is an interruption of real work.
        var (cockpit, sender, target, sent) = _Desk(status);

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        Assert.Equal(AgentWakeOutcome.Busy, outcome);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Wake_OnAPaneNeedingAttention_IsRefusedAsAwaitingItsOperator()
    {
        var (cockpit, sender, target, sent) = _Desk(SessionStatus.NeedsAttention);

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        // Reported as awaiting its operator rather than as busy, because that is what it is — a session with a
        // permission decision outstanding is standing still, and telling the sender "it was working" would be untrue.
        Assert.Equal(AgentWakeOutcome.AwaitingOperator, outcome);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Wake_OnAPaneWaitingForInput_IsAllowed()
    {
        var (cockpit, sender, target, sent) = _Desk(SessionStatus.WaitingForInput);

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        // The operator's decision on this ticket, held here so it survives: a session waiting for input is standing
        // still, and standing still is when a message should be able to reach it. Nothing in the shipping code puts
        // a pane into this state today — NeedsAttention is what a pending permission actually sets — so this test is
        // what keeps the decision true on the day something does.
        Assert.Equal(AgentWakeOutcome.Woken, outcome);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Wake_WhileAConsentQuestionIsOpen_IsRefusedAndLeavesTheQuestionAnswerable()
    {
        // Status Done on purpose: opening a consent banner sets PendingConsent and does not touch SessionStatus, so
        // this is a pane that reads as standing still while a human is being asked something. The status gate waves
        // it through; only the consent gate does not.
        var (cockpit, sender, target, sent) = _Desk(SessionStatus.Done);
        var broker = Substitute.For<IConsentBroker>();
        var promptId = Guid.NewGuid();
        var consent = new ConsentPromptViewModel(
            new ConsentPrompt(
                promptId,
                new ConsentRequest("Delete the branch", "git branch -D main", new ConsentSource(target.PaneId, null, "session"), "git.branch:delete", ConsentRisk.Dangerous),
                CanRemember: false),
            broker);
        Dispatcher.UIThread.Invoke(() => target.PendingConsent = consent);

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        Assert.Equal(AgentWakeOutcome.AwaitingOperator, outcome);
        Assert.Empty(sent);

        // The question is untouched, and answering it still reaches the broker with the answer the operator gave —
        // not displaced, not answered for them, not expired.
        Assert.Same(consent, target.PendingConsent);
        consent.ApproveCommand.Execute(null);
        broker.Received(1).Respond(promptId, ConsentOutcome.Approved, false);
    }

    [Fact]
    public async Task Wake_OnAPaneThatCannotTakeAPrompt_IsRefusedRatherThanReportedAsWoken()
    {
        var (cockpit, sender, target) = Dispatcher.UIThread.Invoke(() =>
        {
            var vm = new CockpitViewModel();
            var from = new TtyViewModel();
            // No PromptSink: the terminal has not been launched, so there is nowhere to type. Reported as a refusal
            // rather than as a wake, because "woken" on a pane that heard nothing is the failure this line exists to
            // avoid, one turn further along.
            var to = new TtyViewModel { SessionStatus = SessionStatus.Idle };
            vm.Sessions.Add(from);
            vm.Sessions.Add(to);
            return (vm, from, to);
        });

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        Assert.Equal(AgentWakeOutcome.CannotTakeATurn, outcome);
    }

    [Fact]
    public async Task Wake_OnASessionPaneWhoseRuntimeNeverStarted_IsRefused()
    {
        var (cockpit, sender, target) = Dispatcher.UIThread.Invoke(() =>
        {
            var vm = new CockpitViewModel();
            var from = new TtyViewModel();
            // Wired for delivery but never started. Its send path accepts a turn and hands back a completed task
            // with nothing having gone anywhere, so only the readiness check separates this from a false wake.
            var to = new SessionViewModel(
                Substitute.For<ISessionManager>(),
                turnInboxDelivery: Substitute.For<IAgentTurnInboxDelivery>())
            {
                SessionStatus = SessionStatus.Idle,
            };
            vm.Sessions.Add(from);
            vm.Sessions.Add(to);
            return (vm, from, to);
        });

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        Assert.Equal(AgentWakeOutcome.CannotTakeATurn, outcome);
    }

    [Fact]
    public async Task Wake_AcrossAWorkspaceBoundary_IsRefusedHostSide()
    {
        var (cockpit, sender, target, sent) = Dispatcher.UIThread.Invoke(() =>
        {
            var vm = new CockpitViewModel();
            var from = new TtyViewModel { WorkspaceId = "ws-1" };
            var to = new TtyViewModel { WorkspaceId = "ws-2", SessionStatus = SessionStatus.Done };
            var captured = new List<string>();
            to.PromptSink = text => captured.Add(text);
            vm.Sessions.Add(from);
            vm.Sessions.Add(to);
            return (vm, from, to, captured);
        });

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        // The target is live, standing still and perfectly wakeable — everything except a neighbour. Asked of the
        // host's own answer to "who is on this caller's desk", never of anything the caller supplied, and asked here
        // rather than only at send time so a pane moved between the two cannot slip through.
        Assert.Equal(AgentWakeOutcome.NotOnDesk, outcome);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Wake_OnAPaneThatIsNoLongerThere_IsRefused()
    {
        var (cockpit, sender, _, _) = _Desk();

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, "pane-that-never-existed", "branch");

        Assert.Equal(AgentWakeOutcome.PaneGone, outcome);
    }
}
