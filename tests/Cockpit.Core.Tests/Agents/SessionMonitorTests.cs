using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Assistant;
using Cockpit.Core.Delegation;
using Cockpit.Core.Notifications;
using NSubstitute;

namespace Cockpit.Core.Tests.Agents;

// AC-1312. Every test here drives `RunOnceAsync` against a hand-picked session list and a substituted inbox: no
// cockpit, no UI thread, no timer — the same seam `CiWatcherTests` uses. The three acceptance criteria are pairs,
// and the counterproof of each is the test that would go red on the obvious wrong implementation.
public class SessionMonitorTests
{
    private readonly IAgentMessageInbox _inbox = Substitute.For<IAgentMessageInbox>();
    private readonly INotificationSettingsStore _settings = Substitute.For<INotificationSettingsStore>();

    public SessionMonitorTests() =>
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new NotificationSettings());

    private static MonitoredSession _Session(string paneId, SessionStatus status, int abandoned = 0, int? rows = null) =>
        new(paneId, $"work on {paneId}", status, abandoned, rows);

    // AC-1313: a delegated task as `list_delegated_tasks` reports it — the background work that has no pane.
    private static DelegatedTaskView _Task(
        string taskId,
        DelegatedTaskStatus status,
        DateTimeOffset started,
        string? owner = "pane-1") =>
        new(taskId, "local-review", $"review for {owner}", "review", status, started, started, null, 1, null,
            status is DelegatedTaskStatus.Failed ? "the model refused" : null, owner);

    private SessionMonitor _Monitor(params MonitoredSession[] sessions) => _Monitor(() => sessions);

    private SessionMonitor _Monitor(Func<IReadOnlyList<MonitoredSession>> watching) =>
        new(_inbox, _settings)
        {
            Watching = watching,
            Tail = paneId => Task.FromResult<IReadOnlyList<string>>([$"the last thing {paneId} wrote"]),
        };

    // Criterion 1: a crashed turn is reported, to the assistant, with the session, what happened and the last rows —
    // everything needed to act without reading the transcript back.
    [Fact]
    public async Task ACrashedTurn_IsReportedOnceWithEnoughToActOn()
    {
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.Failed));

        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            AssistantIdentity.PaneId,
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("pane-1")
                && body.Contains("work on pane-1")
                && body.Contains("ended in an error")
                && body.Contains("the last thing pane-1 wrote")));
    }

    // Counterproof to criterion 1: a session that stays crashed stays quiet. Without this the monitor repeats itself
    // every 30 seconds until someone closes the pane, which is how a safety net gets ignored.
    [Fact]
    public async Task ACrashedTurnThatStaysCrashed_IsNotReportedASecondTime()
    {
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.Failed));

        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // Counterproof to criterion 1: a session that closed its turn properly is not news. The four signals are states
    // that went wrong, not a running commentary on every session that stops working.
    [Fact]
    public async Task ASessionThatFinishedItsTurn_IsNotReportedAtAll()
    {
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.Done), _Session("pane-2", SessionStatus.Idle));

        await monitor.RunOnceAsync();

        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }

    // Criterion 2: three sessions failing at once are one message. A message wakes the assistant, so three would be
    // three turns for one cause — the machine restart of 2026-09-10 took four sessions down at the same moment.
    [Fact]
    public async Task ThreeSessionsFailingInOneTick_AreOneMessageThatNamesAllThree()
    {
        using var monitor = _Monitor(
            _Session("pane-1", SessionStatus.Failed),
            _Session("pane-2", SessionStatus.NeedsAttention),
            _Session("pane-3", SessionStatus.Idle, abandoned: 4));

        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            AssistantIdentity.PaneId,
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("3 sessions")
                && body.Contains("pane-1")
                && body.Contains("pane-2")
                && body.Contains("pane-3")));
    }

    // Counterproof to criterion 2: one finding still reads as that one session. A bundle header over a single
    // session would make the ordinary case look like an incident.
    [Fact]
    public async Task OneSessionInATick_IsReportedAsThatSessionAndNotAsABundle()
    {
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.NeedsAttention));

        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(body => body.StartsWith("Session 'work on pane-1'", StringComparison.Ordinal)
                && !body.Contains("sessions need looking at")));
    }

    // Counterproof to criterion 2: the tick is the bundling unit and nothing is held back. An implementation that
    // waits for a window before sending would collapse these three into one and go red here.
    [Fact]
    public async Task ThreeSessionsFailingOverThreeTicks_AreThreeMessages()
    {
        var live = new List<MonitoredSession> { _Session("pane-1", SessionStatus.Failed) };
        using var monitor = _Monitor(() => live);

        await monitor.RunOnceAsync();
        live.Add(_Session("pane-2", SessionStatus.Failed));
        await monitor.RunOnceAsync();
        live.Add(_Session("pane-3", SessionStatus.Failed));
        await monitor.RunOnceAsync();

        _inbox.Received(3).Deliver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // The one failure here that looks like health from outside: a monitor silenced by a wrong window count is
    // indistinguishable from one with nothing to report. Both halves are pinned — over the ceiling nothing goes out
    // *and* nothing is written back, so the same finding is still said once the window has rolled. Held, not lost.
    [Fact]
    public async Task AFindingOverTheHourlyCeiling_IsHeldAndSaidOnceTheWindowHasRolled()
    {
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new NotificationSettings { MonitorMessagesPerHour = 1 });
        var now = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
        var live = new List<MonitoredSession> { _Session("pane-1", SessionStatus.Failed) };
        using var monitor = _Monitor(() => live);
        monitor.Clock = () => now;

        await monitor.RunOnceAsync();
        live.Add(_Session("pane-2", SessionStatus.Failed));
        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        _inbox.DidNotReceive().Deliver(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Is<string>(body => body.Contains("pane-2")));

        now = now.AddMinutes(61);
        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Is<string>(body => body.Contains("pane-2")));
    }

    // Criterion 3: the boundary against `watch_session`. A pane the assistant armed for `needs-attention` hears that
    // event from the watch, so the monitor says nothing about it.
    [Fact]
    public async Task APaneArmedForNeedsAttention_DoesNotHearThatEventFromTheMonitorAsWell()
    {
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.NeedsAttention));
        monitor.Armed = (paneId, @event) => paneId == "pane-1" && @event == SessionWatchEvents.NeedsAttention;

        await monitor.RunOnceAsync();

        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }

    // Counterproof to criterion 3: the boundary runs per event, not per session. Skipping a watched session outright
    // would drop the one signal no watch covers, on exactly the session someone believed was covered.
    [Fact]
    public async Task APaneArmedForNeedsAttention_StillHearsAboutItsAbandonedProcesses()
    {
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.NeedsAttention, abandoned: 4));
        monitor.Armed = (paneId, @event) => paneId == "pane-1" && @event == SessionWatchEvents.NeedsAttention;

        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("4 of its processes")
                && !body.Contains("nobody has answered")));
    }

    // AC-1313 criterion 4: a session that really hangs — it writes nothing and nothing of its own is running — is
    // said once, with how long it has been quiet, and is not said again while it stays that way.
    [Fact]
    public async Task ASessionThatHasWrittenNothing_IsReportedOnceWithHowLongItHasBeenQuiet()
    {
        var now = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.Busy, rows: 12));
        monitor.Clock = () => now;

        await monitor.RunOnceAsync();
        now = now.AddMinutes(11);
        await monitor.RunOnceAsync();
        now = now.AddMinutes(11);
        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            AssistantIdentity.PaneId,
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("pane-1") && body.Contains("written nothing for 11 minutes")));
    }

    // Counterproof to criterion 4, and the test that carries this ticket: a session that legitimately waits is
    // worth nothing at all. Three ways of waiting, none of which the session has to remember to declare — one on a
    // test run of its own, one on a delegated review it started, one whose rows this cockpit cannot count.
    [Fact]
    public async Task SessionsThatAreLegitimatelyWaiting_AreNotReportedHoweverLongTheyAreQuiet()
    {
        var now = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
        using var monitor = _Monitor(
            _Session("pane-1", SessionStatus.Busy, rows: 12) with { HasOutstandingWork = true },
            _Session("pane-2", SessionStatus.Busy, rows: 12),
            _Session("pane-3", SessionStatus.Busy));
        monitor.Clock = () => now;
        monitor.Tasks = () => [_Task("task-1", DelegatedTaskStatus.Running, now.AddMinutes(-5), owner: "pane-2")];

        await monitor.RunOnceAsync();
        now = now.AddMinutes(11);
        await monitor.RunOnceAsync();

        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }

    // Criterion 5: a threshold of the operator's own for one pane wins over the global one, and only for that pane.
    [Fact]
    public async Task APaneAllowedAnHourOfSilence_IsNotReportedAfterTenMinutesWhileAPlainOneIs()
    {
        var now = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
        using var monitor = _Monitor(
            _Session("pane-1", SessionStatus.Busy, rows: 12) with { SilenceAfter = TimeSpan.FromMinutes(60) },
            _Session("pane-2", SessionStatus.Busy, rows: 12));
        monitor.Clock = () => now;

        await monitor.RunOnceAsync();
        now = now.AddMinutes(11);
        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("pane-2") && !body.Contains("pane-1")));
    }

    // Counterproof to criterion 5: muting is not a very high threshold. A parked pane is worth nothing on any
    // signal — the silence it is parked in, and the crashed turn it was parked with.
    [Fact]
    public async Task AMutedPane_IsReportedNeitherForItsSilenceNorForItsCrashedTurn()
    {
        var now = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.Failed, rows: 12) with { Muted = true });
        monitor.Clock = () => now;

        await monitor.RunOnceAsync();
        now = now.AddMinutes(61);
        await monitor.RunOnceAsync();

        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }

    // Criterion 6: a delegated task has no pane and no statusline, so nothing else can report it. The message names
    // the pane sitting waiting for its answer, and says it once however long the task stays failed.
    [Fact]
    public async Task ADelegatedTaskThatFailed_IsReportedOnceAndNamesThePaneWaitingOnIt()
    {
        var now = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.Idle));
        monitor.Clock = () => now;
        monitor.Tasks = () => [_Task("task-1", DelegatedTaskStatus.Failed, now.AddMinutes(-5), owner: "pane-1")];

        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            AssistantIdentity.PaneId,
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("task-1")
                && body.Contains("pane pane-1 is waiting on it")
                && body.Contains("the model refused")));
    }

    // Counterproof to criterion 6: a task that is still working within its threshold, and one that is already done,
    // are both worth nothing. Without this the monitor reports every delegated task twice over its life.
    [Fact]
    public async Task ADelegatedTaskStillRunningOrAlreadyFinished_IsNotReportedAtAll()
    {
        var now = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
        using var monitor = _Monitor(_Session("pane-1", SessionStatus.Idle));
        monitor.Clock = () => now;
        monitor.Tasks = () =>
        [
            _Task("task-1", DelegatedTaskStatus.Running, now.AddMinutes(-5)),
            _Task("task-2", DelegatedTaskStatus.Completed, now.AddMinutes(-90)),
        ];

        await monitor.RunOnceAsync();

        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }
}
