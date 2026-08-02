using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// The dashboard widget's per-instance count (#AC-18): a dashboard pane is sized by hand, so how many pull
// requests it lists is its own, not the plugin-wide section count — and a stored value that has drifted out of
// range (an older config, a hand edit) must not leave the pane showing nothing or everything.
public class GitHubPullRequestsWidgetConfigTests
{
    [Fact]
    public void AFreshWidget_ShowsTen()
    {
        // The default is roomier than the side strip's five: a dashboard pane has the space for it.
        Assert.Equal(10, GitHubPullRequestsWidgetConfig.Default.MaxItems);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(21, 20)]
    [InlineData(1000, 20)]
    public void AnOutOfRangeCount_IsClampedIntoOneToTwenty(int stored, int expected)
    {
        Assert.Equal(expected, new GitHubPullRequestsWidgetConfig { MaxItems = stored }.Sanitized().MaxItems);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20)]
    public void ACountInRange_IsLeftAlone(int stored)
    {
        Assert.Equal(stored, new GitHubPullRequestsWidgetConfig { MaxItems = stored }.Sanitized().MaxItems);
    }

    [Fact]
    public void TheCount_SurvivesTheRestart()
    {
        var storage = new InMemoryStorage();
        storage.Set(GitHubPullRequestsWidgetConfig.StorageKey, new GitHubPullRequestsWidgetConfig { MaxItems = 15 });

        var afterRestart = storage.Get<GitHubPullRequestsWidgetConfig>(GitHubPullRequestsWidgetConfig.StorageKey);

        Assert.Equal(15, afterRestart!.MaxItems);
    }

    private sealed class InMemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? (T?)value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
