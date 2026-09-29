using NSubstitute;
using Cockpit.Plugin.GitHubPullRequests.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-1396 acceptance 2: the merge watcher ticks off an injected TimeProvider rather than Avalonia's DispatcherTimer,
// so each tick of a fake clock buys exactly one look — and no tick buys none, which the counting search proves.
public class MergedPullRequestWatcherTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void EachTickOfTheClock_IsOneLook_AndNoTickIsNone(int ticks, int expectedLooks)
    {
        var clock = new TickingClock();
        var looks = 0;

        using var watcher = new MergedPullRequestWatcher(
            Substitute.For<ICockpitHost>(),
            _ =>
            {
                looks++;
                return Task.FromResult<IReadOnlyList<GitHubPullRequest>>([]);
            },
            clock);
        clock.Tick(ticks);

        Assert.Equal(expectedLooks, looks);
    }

    public static TheoryData<Action<IDisposable>, int> DisposeBeforeTheAnswer => new()
    {
        { watcher => watcher.Dispose(), 0 },
        { _ => { }, 1 },
    };

    // AC-1416: a look that is already waiting on gh when the watcher is disposed must not start a flow with the
    // answer it then gets.
    [Theory]
    [MemberData(nameof(DisposeBeforeTheAnswer))]
    public void ALookInFlight_RaisesTheTriggerOnlyIfTheWatcherWasNotDisposedFirst(
        Action<IDisposable> beforeTheAnswer,
        int expectedTriggers)
    {
        var clock = new TickingClock();
        var host = Substitute.For<ICockpitHost>();
        var answer = new TaskCompletionSource<IReadOnlyList<GitHubPullRequest>>();
        var searches = 0;
        var merged = new GitHubPullRequest(7, "Ship it", "https://github.com/o/r/pull/7", null, "o/r", "octocat");

        using var watcher = new MergedPullRequestWatcher(
            host,
            _ => Interlocked.Increment(ref searches) == 1
                ? Task.FromResult<IReadOnlyList<GitHubPullRequest>>([])
                : answer.Task,
            clock);

        // Tick one primes; tick two is the look that is left waiting on the answer.
        clock.Tick(2);
        beforeTheAnswer(watcher);
        answer.SetResult([merged]);

        host.Received(expectedTriggers).RaiseWorkflowTrigger(
            PullRequestWorkflowSteps.MergedTrigger,
            Arg.Any<IReadOnlyDictionary<string, string>>());
    }

    // A clock whose timers fire only when the test says so; the watcher's search completes synchronously, so one
    // tick is one whole look.
    private sealed class TickingClock : TimeProvider
    {
        private readonly List<TimerCallback> _callbacks = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callbacks.Add(callback);
            return Substitute.For<ITimer>();
        }

        public void Tick(int times)
        {
            foreach (var callback in Enumerable.Repeat(_callbacks, times).SelectMany(callbacks => callbacks))
            {
                callback(null);
            }
        }
    }
}
