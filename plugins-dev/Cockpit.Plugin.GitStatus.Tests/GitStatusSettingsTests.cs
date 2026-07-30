
namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// <see cref="GitStatusSettings.ShowBranchName"/> (AC-36): defaults to on so the badge keeps showing the branch name
/// until the operator turns it off, and round-trips a saved choice. Also covers AC-522's fourth acceptance
/// criterion: an install from before that ticket may still carry the removed repository list under the old
/// "repos" storage key, and loading must not choke on it.
/// </summary>
public class GitStatusSettingsTests
{
    [Fact]
    public void ShowBranchName_DefaultsToTrue_WhenNothingSaved()
    {
        Assert.True(new GitStatusSettings(new InMemoryPluginStorage()).ShowBranchName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShowBranchName_RoundTrips(bool value)
    {
        var settings = new GitStatusSettings(new InMemoryPluginStorage());

        settings.ShowBranchName = value;

        Assert.Equal(value, settings.ShowBranchName);
    }

    [Fact]
    public void Constructing_DoesNotThrow_WhenStorageStillCarriesTheRemovedRepoList()
    {
        var storage = new InMemoryPluginStorage();
        storage.Set("repos", new List<string> { "/some/legacy/repo" });

        var settings = new GitStatusSettings(storage);

        Assert.True(settings.ShowBranchName);
    }
}
