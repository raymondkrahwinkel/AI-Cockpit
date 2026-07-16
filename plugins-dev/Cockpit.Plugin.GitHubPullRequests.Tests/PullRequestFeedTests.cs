using Cockpit.Plugins.Abstractions;
using FluentAssertions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// The shared fetch behind both the side section and the dashboard widget (#AC-18). The one branch that reaches
/// a verdict without shelling out to gh or hitting the network is the misconfigured HTTP mode — and it is the
/// one that matters most to get right, because an empty list there would read as "no open pull requests" rather
/// than "nothing is set up", which is a lie the operator acts on.
/// </summary>
public class PullRequestFeedTests
{
    [Fact]
    public async Task HttpMode_WithNoRepository_ReportsMissingRatherThanAnEmptyList()
    {
        var settings = new GitHubPullRequestsSettings(new InMemoryStorage())
        {
            UseGitHubCli = false,
            // Owner and Repo left unset.
        };

        var result = await new PullRequestFeed().LoadAsync(settings, forceRefresh: true, CancellationToken.None);

        result.RepositoryMissing.Should().BeTrue();
        result.PullRequests.Should().BeEmpty();
        result.ReviewRequested.Should().BeEmpty();
    }

    [Fact]
    public async Task HttpMode_WithOnlyAnOwner_IsStillMissing()
    {
        // One half of owner/repo is not enough to query a repository — the feed must not try with a blank repo.
        var settings = new GitHubPullRequestsSettings(new InMemoryStorage())
        {
            UseGitHubCli = false,
            Owner = "octocat",
        };

        var result = await new PullRequestFeed().LoadAsync(settings, forceRefresh: true, CancellationToken.None);

        result.RepositoryMissing.Should().BeTrue();
    }

    private sealed class InMemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? (T?)value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
