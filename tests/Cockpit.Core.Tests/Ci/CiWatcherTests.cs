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

    private CiWatcher _Watcher(params string[] answers)
    {
        var remaining = new Queue<string>(answers);
        var watcher = new CiWatcher(_notifier, _inbox, _settings)
        {
            Watching = () => [new WatchedCheckout("pane-1", "AC-634", Environment.CurrentDirectory)],
            Probe = (_, _) => Task.FromResult(remaining.Count > 0 ? remaining.Dequeue() : string.Empty),
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
