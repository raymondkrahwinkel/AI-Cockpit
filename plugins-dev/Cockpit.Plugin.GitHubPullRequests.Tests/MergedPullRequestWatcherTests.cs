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
