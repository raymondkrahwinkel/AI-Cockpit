using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Core.Tests.Agents;

// AC-656. Every test here drives `RunOnceAsync` against a hand-picked pane list and a substituted inbox/gateway: no
// cockpit, no UI thread, no timer — and, the point of the ticket, no model anywhere in the loop that decides whether
// to give a pane a turn.
public class InboxWakeSchedulerTests
{
    private readonly IAgentMessageInbox _inbox = Substitute.For<IAgentMessageInbox>();
    private readonly IWorkspaceAgentGateway _gateway = Substitute.For<IWorkspaceAgentGateway>();

    private InboxWakeScheduler _Scheduler(params string[] panes) =>
        new(_inbox, _gateway) { Panes = () => panes };

    private static AgentMessage _Message(string id, string from, string to, string kind = "heads-up") =>
        new(id, from, to, kind, "body", DateTimeOffset.UtcNow);

    // Criterion: a tick over panes with nothing waiting costs nothing — the gateway is never asked to wake anyone.
    [Fact]
    public async Task ATickOverPanesWithNoWaitingMail_CostsNothing()
    {
        _inbox.PeekOldest(Arg.Any<string>()).Returns((AgentMessage?)null);
        var scheduler = _Scheduler("pane-1", "pane-2");

        await scheduler.RunOnceAsync();

        await _gateway.DidNotReceiveWithAnyArgs().TryWakeForWaitingMailAsync(default!, default!, default!);
    }

    // The other half of the same criterion: no panes to check at all, not even an inbox lookup.
    [Fact]
    public async Task ATickOverNoPanes_TouchesNeitherTheInboxNorTheGateway()
    {
        var scheduler = _Scheduler();

        await scheduler.RunOnceAsync();

        _inbox.DidNotReceiveWithAnyArgs().PeekOldest(default!);
        await _gateway.DidNotReceiveWithAnyArgs().TryWakeForWaitingMailAsync(default!, default!, default!);
    }

    // Criterion: mail waiting for a pane starts a turn for it, through the host-triggered path — not the sender's
    // urgent-notify one — carrying the message's own sender and kind.
    [Fact]
    public async Task APaneWithWaitingMail_IsWoken()
    {
        _inbox.PeekOldest("pane-1").Returns(_Message("m1", "pane-sender", "pane-1", "ci"));
        _gateway.TryWakeForWaitingMailAsync("pane-sender", "pane-1", "ci").Returns(AgentWakeOutcome.Woken);
        var scheduler = _Scheduler("pane-1");

        await scheduler.RunOnceAsync();

        await _gateway.Received(1).TryWakeForWaitingMailAsync("pane-sender", "pane-1", "ci");
    }

    // Dedup: the wake send is fire-and-forget, so the pane's status may not have moved by the very next tick. A
    // second attempt for the same still-oldest message before it is ever taken would risk starting a second turn.
    [Fact]
    public async Task APaneAlreadyWokenForItsOldestMessage_IsNotWokenAgain_UntilThatMessageIsGone()
    {
        var message = _Message("m1", "pane-sender", "pane-1", "ci");
        _inbox.PeekOldest("pane-1").Returns(message);
        _gateway.TryWakeForWaitingMailAsync("pane-sender", "pane-1", "ci").Returns(AgentWakeOutcome.Woken);
        var scheduler = _Scheduler("pane-1");

        await scheduler.RunOnceAsync();
        await scheduler.RunOnceAsync();
        await scheduler.RunOnceAsync();

        await _gateway.Received(1).TryWakeForWaitingMailAsync("pane-sender", "pane-1", "ci");

        // The message was taken (by the turn it started, or by read_inbox) — a fresh one is its own attempt, taking
        // the cumulative count (NSubstitute's Received counts calls made across the whole test, not since the last
        // assertion) from one to two.
        var next = _Message("m2", "pane-sender", "pane-1", "ci");
        _inbox.PeekOldest("pane-1").Returns(next);

        await scheduler.RunOnceAsync();

        await _gateway.Received(2).TryWakeForWaitingMailAsync("pane-sender", "pane-1", "ci");
    }

    // A refusal that is about the pane's current state (busy, awaiting its operator, gone) is not remembered — the
    // next tick tries again for free, because that is exactly the kind of outcome that stops applying on its own.
    [Fact]
    public async Task APaneWhoseWakeWasRefused_IsTriedAgainOnTheNextTick()
    {
        var message = _Message("m1", "pane-sender", "pane-1", "ci");
        _inbox.PeekOldest("pane-1").Returns(message);
        _gateway.TryWakeForWaitingMailAsync("pane-sender", "pane-1", "ci").Returns(AgentWakeOutcome.Busy);
        var scheduler = _Scheduler("pane-1");

        await scheduler.RunOnceAsync();
        await scheduler.RunOnceAsync();

        await _gateway.Received(2).TryWakeForWaitingMailAsync("pane-sender", "pane-1", "ci");
    }

    // Every live pane is checked without anything having to arm it first — the point being that this is not
    // SessionWatcher's opt-in shape.
    [Fact]
    public async Task EveryPaneIsCheckedWithNoArmingStep()
    {
        _inbox.PeekOldest("pane-1").Returns(_Message("m1", "pane-sender", "pane-1"));
        _inbox.PeekOldest("pane-2").Returns(_Message("m2", "pane-sender", "pane-2"));
        _gateway.TryWakeForWaitingMailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(AgentWakeOutcome.Woken);
        var scheduler = _Scheduler("pane-1", "pane-2");

        await scheduler.RunOnceAsync();

        await _gateway.Received(1).TryWakeForWaitingMailAsync(Arg.Any<string>(), "pane-1", Arg.Any<string>());
        await _gateway.Received(1).TryWakeForWaitingMailAsync(Arg.Any<string>(), "pane-2", Arg.Any<string>());
    }

    // A tick with nothing wired at all — Panes never set — is the same free no-op App.axaml.cs's early-return
    // guards mirror in SessionWatcher/CiWatcher.
    [Fact]
    public async Task ATickWithNoPanesFuncWired_IsANoOp()
    {
        var scheduler = new InboxWakeScheduler(_inbox, _gateway);

        await scheduler.RunOnceAsync();

        _inbox.DidNotReceiveWithAnyArgs().PeekOldest(default!);
    }

    // Asked of the container rather than of the class: an unregistered scheduler resolves to null in App.axaml.cs,
    // which checks nothing and wakes no one — the whole feature dead with every test still green.
    [Fact]
    public async Task TheContainer_ResolvesTheScheduler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Core.DependencyInjection).Assembly,
            typeof(Infrastructure.DependencyInjection).Assembly,
            typeof(InboxWakeScheduler).Assembly);

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<InboxWakeScheduler>());
    }
}
