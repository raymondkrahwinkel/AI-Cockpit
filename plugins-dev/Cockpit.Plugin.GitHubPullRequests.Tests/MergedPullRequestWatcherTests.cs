using Avalonia.Threading;
using System.Reflection;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

[Collection("avalonia")]
public class MergedPullRequestWatcherTests
{
    [Fact]
    public void Constructor_StartsThePeriodicTimer() => HeadlessAvalonia.Run(() =>
    {
        using var watcher = new MergedPullRequestWatcher(new TestBadgeHost());
        var timer = Assert.IsType<DispatcherTimer>(typeof(MergedPullRequestWatcher)
            .GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(watcher));

        Assert.True(timer.IsEnabled);
    });
}
