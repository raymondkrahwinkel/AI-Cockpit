using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;
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
    /// A pane that both takes prompts and carries mail on its turns. A running session pane is both in production —
    /// dependency injection always hands it the delivery seam — but not one this test can build: making it answer
    /// true to <see cref="SessionPanelViewModel.CanTakeAPrompt"/> takes a live driver, and a terminal pane, which
    /// needs nothing but a sink, has no turn to hang delivery on. Without a pane that is both, the gateway could
    /// pass a hard-coded <c>false</c> into the notice and every test here would still be green, while a woken agent
    /// was told to go and read an inbox whose contents were already in front of it.
    /// </summary>
    private sealed class DeliveringTerminal : TtyViewModel
    {
        public override bool DeliversInboxAtTurnStart => true;
    }

    /// <summary>Captures what the gateway logged, so a wake whose turn failed can be shown to leave a trace rather than a swallowed exception.</summary>
    private sealed class CapturingLogger : ILogger<WorkspaceAgentGateway>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
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
        new(cockpit, new WorkspaceAgentCoordinator(), NullLogger<WorkspaceAgentGateway>.Instance);

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
    public async Task Wake_OnAPaneWaitingForInput_IsRefusedAsAwaitingOperator()
    {
        var (cockpit, sender, target, sent) = _Desk(SessionStatus.WaitingForInput);

        var outcome = await _Gateway(cockpit).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        // Reversed by AC-615, deliberately and not quietly. AC-395 put this status in the wakeable set on the
        // reading that a session waiting for input is standing still, and that was defensible while wake was
        // something each session had to opt into: only a pane that had chosen it could be reached this way.
        //
        // Raymond's decision of 2026-07-31 moves the consent to the operator and turns it on by default, and that
        // changes what this status costs. WaitingForInput means a tool-use permission decision is pending or the CLI
        // asked for something — a question in front of a human, the same signal NeedsAttention carries. Under
        // default-on it would reach every session on the desk, so the first thing an agent could do with the wake
        // route is talk over the decision its operator is standing at.
        Assert.Equal(AgentWakeOutcome.AwaitingOperator, outcome);
        Assert.Empty(sent);
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

    [Fact]
    public async Task Wake_IsRefusedTheSecondTime_BecauseTheFirstOneLeftThePaneWorking()
    {
        var (cockpit, sender, target, sent) = _Desk();
        var gateway = _Gateway(cockpit);

        var first = await gateway.TryWakeAsync(sender.PaneId, target.PaneId, "branch");
        var second = await gateway.TryWakeAsync(sender.PaneId, target.PaneId, "worktree");

        // The gate reads the pane's status, and until the wake itself marked the turn the pane went on reporting
        // itself idle for the whole of it — so a second urgent message from anyone walked straight through onto a
        // session that was already answering the first. A terminal pane infers its status from what its CLI prints,
        // so the window lasted until the first line came back.
        Assert.Equal(AgentWakeOutcome.Woken, first);
        Assert.Equal(AgentWakeOutcome.Busy, second);
        Assert.Single(sent);
    }

    [Fact]
    public async Task Wake_WhoseTurnThrowsOnItsWayOut_LeavesATraceInsteadOfAnUnobservedFailure()
    {
        var (cockpit, sender, target) = Dispatcher.UIThread.Invoke(() =>
        {
            var vm = new CockpitViewModel();
            var from = new TtyViewModel();
            var to = new TtyViewModel { SessionStatus = SessionStatus.Done };
            to.PromptSink = _ => throw new IOException("the terminal went away");
            vm.Sessions.Add(from);
            vm.Sessions.Add(to);
            return (vm, from, to);
        });
        var logger = new CapturingLogger();

        // The send is deliberately not awaited by the gateway, so a throw on that path has no caller to surface it:
        // discarded, it becomes an unobserved exception at some later garbage collection, attributed to nothing.
        var outcome = await new WorkspaceAgentGateway(cockpit, new WorkspaceAgentCoordinator(), logger).TryWakeAsync(sender.PaneId, target.PaneId, "branch");

        Assert.Equal(AgentWakeOutcome.Woken, outcome);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<IOException>(entry.Exception);
    }
}
