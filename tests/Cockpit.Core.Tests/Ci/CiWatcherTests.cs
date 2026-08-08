using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Assistant;
using Cockpit.Core.Notifications;
using Cockpit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Core.Tests.Ci;

// AC-634. Every test here drives `RunOnceAsync` with a stubbed probe: no gh, no network, and — the point of the
// ticket — no model anywhere in the loop that decides whether to speak up.
public class CiWatcherTests
{
    private const string AllGreen = """[{"bucket":"pass","link":"","name":"build","workflow":"CI"}]""";

    private const string OneRed = """
        [
          {"bucket":"pass","link":"","name":"build","workflow":"CI"},
          {"bucket":"fail","link":"https://github.com/o/r/actions/runs/1/job/2","name":"plugins","workflow":"CI"}
        ]
        """;

    private readonly IAttentionNotifier _notifier = Substitute.For<IAttentionNotifier>();
    private readonly IAgentMessageInbox _inbox = Substitute.For<IAgentMessageInbox>();
    private readonly INotificationSettingsStore _settings = Substitute.For<INotificationSettingsStore>();

    public CiWatcherTests() =>
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new NotificationSettings { NotifyOnCiFailure = true });

    // AC-645: what `gh pr view --json reviewDecision,mergeable` prints. The empty review decision is a repository
    // that requires no review, which is not the same thing as a review that has not happened yet.
    private const string Mergeable = """{"mergeable":"MERGEABLE","reviewDecision":""}""";

    private const string Approved = """{"mergeable":"MERGEABLE","reviewDecision":"APPROVED"}""";

    private const string ChangesRequested = """{"mergeable":"MERGEABLE","reviewDecision":"CHANGES_REQUESTED"}""";

    private const string Conflicting = """{"mergeable":"CONFLICTING","reviewDecision":"APPROVED"}""";

    private const string OnePending = """
        [
          {"bucket":"pass","link":"","name":"build","workflow":"CI"},
          {"bucket":"pending","link":"","name":"plugins","workflow":"CI"}
        ]
        """;

    private CiWatcher _Watcher(params string[] answers) => _Watcher(answers, mergeState: string.Empty);

    private CiWatcher _Watcher(string[] answers, string mergeState)
    {
        var remaining = new Queue<string>(answers);
        var watcher = new CiWatcher(_notifier, _inbox, _settings)
        {
            Watching = () => [new WatchedCheckout("pane-1", "AC-634", Environment.CurrentDirectory)],
            Probe = (_, _) => Task.FromResult(remaining.Count > 0 ? remaining.Dequeue() : string.Empty),
            MergeProbe = (_, _) => Task.FromResult(mergeState),
        };

        return watcher;
    }

    // Acceptance criterion 1: a tick that finds nothing says nothing, to anyone. The notifier and the inbox are the
    // only two things this can spend, and neither is touched.
    [Fact]
    public async Task ATickThatFindsNothing_CostsNothingAndSaysNothing()
    {
        using var watcher = _Watcher(AllGreen);

        await watcher.RunOnceAsync();

        await _notifier.DidNotReceiveWithAnyArgs().NotifyAttentionAsync(default!, default);
        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }

    // Acceptance criterion 2: nobody asked, and both the operator and the assistant are told.
    [Fact]
    public async Task ARedCheck_ReachesTheOperatorAndTheAssistantWithoutBeingAskedFor()
    {
        using var watcher = _Watcher(OneRed);

        await watcher.RunOnceAsync();

        await _notifier.Received(1).NotifyAttentionAsync(
            Arg.Is<AttentionNotification>(notification => notification.Body.Contains("plugins")),
            Arg.Any<CancellationToken>());

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            AssistantIdentity.PaneId,
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("plugins")));
    }

    // Criterion 3: the message reports, it does not act. Nothing here starts a session or a task, and the message
    // says so out loud so a reader does not assume something is already under way.
    [Fact]
    public async Task TheMessage_SaysThatNothingHasBeenStarted()
    {
        using var watcher = _Watcher(OneRed);

        await watcher.RunOnceAsync();

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("Nothing has been started")));
    }

    // The failure mode that would get this feature switched off: red stays red until someone fixes it, and a watcher
    // on a five-minute timer would otherwise repeat itself all afternoon.
    [Fact]
    public async Task ACheckThatStaysRed_IsReportedOnceAndNotAgain()
    {
        using var watcher = _Watcher(OneRed, OneRed, OneRed);

        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();

        await _notifier.Received(1).NotifyAttentionAsync(Arg.Any<AttentionNotification>(), Arg.Any<CancellationToken>());
        _inbox.Received(1).Deliver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ACheckFixedAndBrokenAgain_IsReportedAgain()
    {
        using var watcher = _Watcher(OneRed, AllGreen, OneRed);

        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();

        await _notifier.Received(2).NotifyAttentionAsync(Arg.Any<AttentionNotification>(), Arg.Any<CancellationToken>());
    }

    // Switched off means no gh is started, not that its answer is thrown away — the cost of this feature is the
    // processes it spawns, and that is what the switch has to stop.
    [Fact]
    public async Task WithTheSettingOff_NothingIsEvenLookedAt()
    {
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new NotificationSettings { NotifyOnCiFailure = false });

        var probed = false;
        using var watcher = new CiWatcher(_notifier, _inbox, _settings)
        {
            Watching = () => [new WatchedCheckout("pane-1", "AC-634", Environment.CurrentDirectory)],
            Probe = (_, _) =>
            {
                probed = true;
                return Task.FromResult(OneRed);
            },
        };

        await watcher.RunOnceAsync();

        Assert.False(probed);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAttentionAsync(default!, default);
    }

    // Two sessions on one worktree are one branch and one set of checks. Reported per pane, the operator would be
    // told the same failure once per session sitting on it.
    [Fact]
    public async Task TwoSessionsOnOneCheckout_AreOneCheckAndOneReport()
    {
        var probes = 0;
        using var watcher = new CiWatcher(_notifier, _inbox, _settings)
        {
            Watching = () =>
            [
                new WatchedCheckout("pane-1", "AC-634", Environment.CurrentDirectory),
                new WatchedCheckout("pane-2", "AC-634 review", Environment.CurrentDirectory),
            ],
            Probe = (_, _) =>
            {
                probes++;
                return Task.FromResult(OneRed);
            },
        };

        await watcher.RunOnceAsync();

        Assert.Equal(1, probes);
        await _notifier.Received(1).NotifyAttentionAsync(Arg.Any<AttentionNotification>(), Arg.Any<CancellationToken>());
    }

    // No gh, no login, no pull request: the watcher survives it and remembers nothing that did not happen, so the
    // next look is a first look rather than a silence that reads as green.
    [Fact]
    public async Task AProbeThatThrows_IsSurvivedAndTheNextLookStillReports()
    {
        var thrown = false;
        using var watcher = new CiWatcher(_notifier, _inbox, _settings)
        {
            Watching = () => [new WatchedCheckout("pane-1", "AC-634", Environment.CurrentDirectory)],
            Probe = (_, _) =>
            {
                if (thrown)
                {
                    return Task.FromResult(OneRed);
                }

                thrown = true;
                throw new InvalidOperationException("gh is not installed");
            },
        };

        await watcher.RunOnceAsync();
        await _notifier.DidNotReceiveWithAnyArgs().NotifyAttentionAsync(default!, default);

        await watcher.RunOnceAsync();
        await _notifier.Received(1).NotifyAttentionAsync(Arg.Any<AttentionNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNothingWiredToWatch_ItDoesNothing()
    {
        using var watcher = new CiWatcher(_notifier, _inbox, _settings);

        await watcher.RunOnceAsync();

        await _settings.DidNotReceiveWithAnyArgs().LoadAsync(default);
    }

    // AC-645, criterion 2: the mirror of a red check, over both channels, without anyone asking.
    [Fact]
    public async Task APullRequestThatGoesGreenAndMergeable_ReachesTheOperatorAndTheAssistant()
    {
        using var watcher = _Watcher([AllGreen], Mergeable);

        await watcher.RunOnceAsync();

        await _notifier.Received(1).NotifyAttentionAsync(
            Arg.Is<AttentionNotification>(notification => notification.Body.Contains("ready to merge")),
            Arg.Any<CancellationToken>());

        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            AssistantIdentity.PaneId,
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("mergeable") && body.Contains("Nothing has been started")));
    }

    // Criterion 2 again, the half that gets a feature switched off: ready stays ready until somebody merges it, and a
    // five-minute timer would otherwise say so all afternoon.
    [Fact]
    public async Task APullRequestLeftSittingReady_IsReportedOnceAndNotAgain()
    {
        using var watcher = _Watcher([AllGreen, AllGreen, AllGreen], Approved);

        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();

        await _notifier.Received(1).NotifyAttentionAsync(Arg.Any<AttentionNotification>(), Arg.Any<CancellationToken>());
        _inbox.Received(1).Deliver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // Criterion 4: green checks are not the same question as "may this be merged".
    [Theory]
    [InlineData(ChangesRequested)]
    [InlineData(Conflicting)]
    public async Task GreenChecksWithSomethingStillBlockingTheMerge_NeverReport(string mergeState)
    {
        using var watcher = _Watcher([AllGreen, AllGreen], mergeState);

        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();

        await _notifier.DidNotReceiveWithAnyArgs().NotifyAttentionAsync(default!, default);
        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }

    // A check still running is not "every check present and green" — reporting here is reporting a run that may
    // still go red in a minute.
    [Fact]
    public async Task AStillRunningCheck_IsNotReady()
    {
        using var watcher = _Watcher([OnePending], Mergeable);

        await watcher.RunOnceAsync();

        await _notifier.DidNotReceiveWithAnyArgs().NotifyAttentionAsync(default!, default);
    }

    // Criterion 1: the cost of this. A red tick and an already-reported-ready tick each start one process, the same
    // as before AC-645 — the second gh only runs for a checkout that is green and not yet known ready.
    [Fact]
    public async Task TheSecondGhCall_OnlyRunsForACheckoutThatIsGreenAndNotYetReported()
    {
        var mergeProbes = 0;
        using var watcher = new CiWatcher(_notifier, _inbox, _settings)
        {
            Watching = () => [new WatchedCheckout("pane-1", "AC-645", Environment.CurrentDirectory)],
            Probe = (_, _) => Task.FromResult(OneRed),
            MergeProbe = (_, _) =>
            {
                mergeProbes++;
                return Task.FromResult(Mergeable);
            },
        };

        await watcher.RunOnceAsync();
        Assert.Equal(0, mergeProbes);

        watcher.Probe = (_, _) => Task.FromResult(AllGreen);
        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();

        Assert.Equal(1, mergeProbes);
    }

    // Criterion 3: a checkout nobody is on any more is forgotten, so it cannot be reported a second time — and a
    // merged pull request is one `gh pr view` answers nothing for, which reads as not ready either way.
    [Fact]
    public async Task ACheckoutNobodyIsOnAnyMore_IsForgotten()
    {
        var watching = true;
        using var watcher = new CiWatcher(_notifier, _inbox, _settings)
        {
            Watching = () => watching
                ? [new WatchedCheckout("pane-1", "AC-645", Environment.CurrentDirectory)]
                : [],
            Probe = (_, _) => Task.FromResult(AllGreen),
            MergeProbe = (_, _) => Task.FromResult(Approved),
        };

        await watcher.RunOnceAsync();
        watching = false;
        await watcher.RunOnceAsync();
        watching = true;
        await watcher.RunOnceAsync();

        // Reported again only because the checkout came back as a checkout the watcher had never seen — which is the
        // point of the cleanup, not a leak: the state does not grow with every session ever closed.
        await _notifier.Received(2).NotifyAttentionAsync(Arg.Any<AttentionNotification>(), Arg.Any<CancellationToken>());
    }

    // A pull request that goes green, gets a push, goes red and green again is news the second time too.
    [Fact]
    public async Task AReadyPullRequestThatGoesRedAndGreenAgain_IsReportedAgain()
    {
        using var watcher = _Watcher([AllGreen, OneRed, AllGreen], Mergeable);

        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();
        await watcher.RunOnceAsync();

        _inbox.Received(2).Deliver(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("mergeable")));
    }

    // Asked of the container rather than of the class: an unregistered watcher resolves to null in `App.axaml.cs`,
    // which starts nothing and says nothing — the whole feature dead with every test still green.
    [Fact]
    public async Task TheContainer_ResolvesTheWatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Core.DependencyInjection).Assembly,
            typeof(Infrastructure.DependencyInjection).Assembly,
            typeof(CiWatcher).Assembly);

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<CiWatcher>());
    }
}
