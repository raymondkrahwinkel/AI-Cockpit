using Cockpit.Plugins.Abstractions;
using FluentAssertions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// The pull requests set aside: the long-lived ones that live in a todo somewhere and do not need to be in front of you
/// every day. Kept across restarts — a PR you ignored today is one you do not want to be looking at tomorrow either —
/// and kept as a <em>list</em>, so ignoring is something you can undo.
/// </summary>
public class IgnoredPullRequestsTests
{
    [Fact]
    public void NothingIsIgnoredUntilSomethingIs()
    {
        var settings = new GitHubPullRequestsSettings(new InMemoryStorage());

        settings.IgnoredPullRequests.Should().BeEmpty();
    }

    [Fact]
    public void IgnoringOne_SurvivesTheRestart()
    {
        var storage = new InMemoryStorage();
        new GitHubPullRequestsSettings(storage)
        {
            IgnoredPullRequests = new HashSet<string>(StringComparer.Ordinal) { "https://github.com/o/r/pull/1" },
        };

        // A fresh settings object over the same storage is what the next launch sees.
        var afterRestart = new GitHubPullRequestsSettings(storage);

        afterRestart.IgnoredPullRequests.Should().Equal("https://github.com/o/r/pull/1");
    }

    [Fact]
    public void ShowingOneAgain_TakesItOffTheList()
    {
        var storage = new InMemoryStorage();
        var settings = new GitHubPullRequestsSettings(storage)
        {
            IgnoredPullRequests = new HashSet<string>(StringComparer.Ordinal)
            {
                "https://github.com/o/r/pull/1",
                "https://github.com/o/r/pull/2",
            },
        };

        settings.IgnoredPullRequests = settings.IgnoredPullRequests
            .Where(url => url != "https://github.com/o/r/pull/1")
            .ToHashSet(StringComparer.Ordinal);

        new GitHubPullRequestsSettings(storage).IgnoredPullRequests.Should().Equal("https://github.com/o/r/pull/2");
    }

    private sealed class InMemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? (T?)value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
