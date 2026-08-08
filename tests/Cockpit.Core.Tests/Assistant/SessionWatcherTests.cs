using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Core.Tests.Assistant;

// AC-640. Every test here drives `RunOnce` against a hand-moved pane: no cockpit, no UI thread, no clock — and, the
// point of the ticket, no model anywhere in the loop that decides whether to speak up.
public class SessionWatcherTests
{
    private const string Pane = "pane-1";

    private readonly IAgentMessageInbox _inbox = Substitute.For<IAgentMessageInbox>();

    private DateTimeOffset _now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    // The pane the watcher is looking at, as the tests move it: a status, an attention flag and a list of transcript
    // rows. Exactly the four things the real probe reads off a live session.
    private sealed class FakePane
    {
        public SessionStatus Status { get; set; } = SessionStatus.Busy;

        public bool NeedsAttention { get; set; }

        public bool HasTranscript { get; set; } = true;

        public List<string> Rows { get; } = [];

        public bool Gone { get; set; }
    }

    private SessionWatcher _Watcher(FakePane pane, Action? onProbe = null)
    {
        var watcher = new SessionWatcher(_inbox)
        {
            Clock = () => _now,
        };

        watcher.Probe = (_, since) =>
        {
            onProbe?.Invoke();
            return pane.Gone
                ? null
                : new WatchedPane(
                    "AC-640 worker",
                    pane.Status,
                    pane.NeedsAttention,
                    pane.HasTranscript,
                    pane.Rows.Count,
                    [.. pane.Rows.Skip(since)],
                    [.. pane.Rows.TakeLast(5)]);
        };

        return watcher;
    }

    private void _Delivered(int times, string contains) =>
        _inbox.Received(times).Deliver(
            Arg.Any<string>(),
            AssistantIdentity.PaneId,
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains(contains, StringComparison.Ordinal)));

    // Criterion 1: nothing armed, nothing spent. The probe is the only thing a tick can cost when the inbox is not
    // touched, and it is not called either.
    [Fact]
    public void ATickOverNoArmedPanes_CostsNothing()
    {
        var probed = false;
        using var watcher = _Watcher(new FakePane(), onProbe: () => probed = true);

        watcher.RunOnce();

        Assert.False(probed);
        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }

    // Criterion 2, and the cross-cutting requirement: one message, naming the pane, the event, and what the session
    // actually said — which is what turns "it stopped" into "it stopped and asked you something".
    [Fact]
    public void APaneThatGoesFromBusyToIdle_IsReportedOnceWithItsLastLines()
    {
        var pane = new FakePane { Status = SessionStatus.Busy };
        using var watcher = _Watcher(pane);
        Assert.True(watcher.Watch(Pane, [SessionWatchEvents.BusyToIdle], null, null).Ok);

        pane.Rows.Add("Which base branch should I cut from?");
        pane.Status = SessionStatus.Idle;
        watcher.RunOnce();

        _Delivered(1, "busy-to-idle");
        _Delivered(1, Pane);
        _Delivered(1, "Which base branch should I cut from?");
    }

    // Criterion 3: a pane left sitting idle is not news every thirty seconds.
    [Fact]
    public void APaneThatStaysIdle_IsNotReportedAgain()
    {
        var pane = new FakePane { Status = SessionStatus.Busy };
        using var watcher = _Watcher(pane);
        watcher.Watch(Pane, [SessionWatchEvents.BusyToIdle], null, null);

        pane.Status = SessionStatus.Done;
        watcher.RunOnce();
        watcher.RunOnce();
        watcher.RunOnce();

        _inbox.Received(1).Deliver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // Criterion 2: the one an agent can never report itself, because it cannot call a tool while it waits.
    [Fact]
    public void APaneThatStopsOnAPermission_IsReported()
    {
        var pane = new FakePane();
        using var watcher = _Watcher(pane);
        watcher.Watch(Pane, [SessionWatchEvents.NeedsAttention], null, null);

        pane.NeedsAttention = true;
        pane.Status = SessionStatus.NeedsAttention;
        watcher.RunOnce();
        watcher.RunOnce();

        _Delivered(1, "needs-attention");
    }

    // Criterion 4: gone is said once and the watch goes with the pane — a later tick over the same id is a no-op
    // rather than a second report, and there is no watch left to disarm.
    [Fact]
    public void APaneThatDisappears_IsReportedOnceAndUnwatchesItself()
    {
        var pane = new FakePane();
        using var watcher = _Watcher(pane);
        watcher.Watch(Pane, [SessionWatchEvents.Gone], null, null);

        pane.Gone = true;
        watcher.RunOnce();
        watcher.RunOnce();

        _Delivered(1, "gone");
        Assert.False(watcher.Unwatch(Pane));
    }

    // The other half of `gone`: a pane that said it had finished and was then closed is not a pane that fell over
    // quietly, and reporting it as one would be the watcher inventing a failure out of an ordinary tidy-up.
    [Fact]
    public void APaneThatReportedFinishingAndIsThenClosed_IsNotReportedAsGone()
    {
        var pane = new FakePane { Status = SessionStatus.Busy };
        using var watcher = _Watcher(pane);
        watcher.Watch(Pane, [SessionWatchEvents.BusyToIdle, SessionWatchEvents.Gone], null, null);

        pane.Status = SessionStatus.Done;
        watcher.RunOnce();
        pane.Gone = true;
        watcher.RunOnce();

        _Delivered(1, "busy-to-idle");
        _Delivered(0, "gone");
    }

    // Criterion 5: disarmed means disarmed now, including out of the tick that is already running — which is what
    // unwatching from inside the probe proves.
    [Fact]
    public void UnwatchingMidTick_StopsTheReportThatTickWasAbout()
    {
        var pane = new FakePane { Status = SessionStatus.Busy };
        SessionWatcher? watcher = null;
        watcher = _Watcher(pane, onProbe: () => watcher?.Unwatch(Pane));

        using (watcher)
        {
            pane.Status = SessionStatus.Busy;
            watcher.Watch(Pane, [SessionWatchEvents.BusyToIdle], null, null);
            pane.Status = SessionStatus.Idle;
            watcher.RunOnce();
        }

        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }

    // Criterion 6, all four refusals. Each one is refused at the call, not swallowed into a watch that then never
    // fires — a watch armed on nothing is worse than no watch, because it reads as coverage.
    [Fact]
    public void ArmingWhatCannotBeWatched_IsRefused()
    {
        var pane = new FakePane { HasTranscript = false };
        using var watcher = _Watcher(pane);

        pane.Gone = true;
        Assert.False(watcher.Watch(Pane, [SessionWatchEvents.BusyToIdle], null, null).Ok);

        pane.Gone = false;
        Assert.False(watcher.Watch(Pane, [SessionWatchEvents.Stuck], null, null).Ok);
        Assert.False(watcher.Watch(Pane, [SessionWatchEvents.Pattern], null, "error").Ok);
        Assert.False(watcher.Watch(Pane, ["finished"], null, null).Ok);
        Assert.False(watcher.Watch(Pane, [], null, null).Ok);

        // And the same pane with a transcript takes the two that need one — so the refusals above are about the
        // transcript and not about the events being unimplemented.
        pane.HasTranscript = true;
        Assert.True(watcher.Watch(Pane, [SessionWatchEvents.Stuck], null, null).Ok);
        Assert.False(watcher.Watch(Pane, [SessionWatchEvents.Pattern], null, "(unclosed").Ok);
        Assert.True(watcher.Watch(Pane, [SessionWatchEvents.Pattern], null, "err(or)?").Ok);
    }

    // Criterion 7: the safety net for a status field that is itself wrong. The status is held at Busy for the whole
    // test and nothing but the row count moves, so an implementation that consulted status could not pass this.
    [Fact]
    public void APaneThatStopsWritingWhileStillClaimingToBeBusy_IsReportedStuck()
    {
        var pane = new FakePane { Status = SessionStatus.Busy };
        pane.Rows.Add("working");
        using var watcher = _Watcher(pane);
        watcher.Watch(Pane, [SessionWatchEvents.Stuck], afterMinutes: 10, null);

        _now = _now.AddMinutes(5);
        watcher.RunOnce();
        _Delivered(0, "stuck");

        _now = _now.AddMinutes(6);
        watcher.RunOnce();
        watcher.RunOnce();

        Assert.Equal(SessionStatus.Busy, pane.Status);
        _Delivered(1, "stuck");
    }

    // Criterion 3 for `stuck`, the other way round: a pane that starts writing again has stopped being stuck, and is
    // news again if it stalls a second time.
    [Fact]
    public void APaneThatStartsWritingAgain_CanBeReportedStuckASecondTime()
    {
        var pane = new FakePane();
        pane.Rows.Add("working");
        using var watcher = _Watcher(pane);
        watcher.Watch(Pane, [SessionWatchEvents.Stuck], afterMinutes: 10, null);

        _now = _now.AddMinutes(11);
        watcher.RunOnce();

        pane.Rows.Add("still here");
        watcher.RunOnce();

        _now = _now.AddMinutes(11);
        watcher.RunOnce();

        _Delivered(2, "stuck");
    }

    // Criterion 8: matched on the rows that arrived after arming, never on the ones that were already there, and a
    // second distinct match is its own report rather than a repeat to be deduped.
    [Fact]
    public void APatternMatches_OnlyRowsAddedAfterArming_AndEveryFreshOneReports()
    {
        var pane = new FakePane();
        pane.Rows.Add("error: this one was already on screen");
        using var watcher = _Watcher(pane);
        watcher.Watch(Pane, [SessionWatchEvents.Pattern], null, "error:");

        watcher.RunOnce();
        _Delivered(0, "pattern");

        pane.Rows.Add("error: the build fell over");
        watcher.RunOnce();
        _Delivered(1, "pattern");
        _Delivered(1, "the build fell over");

        pane.Rows.Add("error: and again");
        watcher.RunOnce();
        _Delivered(2, "pattern");
    }

    // Asked of the container rather than of the class: an unregistered watcher resolves to null in `App.axaml.cs`,
    // which arms nothing and says nothing — the whole feature dead with every test still green.
    [Fact]
    public async Task TheContainer_ResolvesTheWatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Core.DependencyInjection).Assembly,
            typeof(Infrastructure.DependencyInjection).Assembly,
            typeof(SessionWatcher).Assembly);

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<SessionWatcher>());
    }
}
