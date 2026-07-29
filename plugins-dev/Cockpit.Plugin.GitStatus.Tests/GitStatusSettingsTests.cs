
namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// <see cref="GitStatusSettings.ShowBranchName"/> (AC-36): defaults to on so the badge keeps showing the branch name
/// until the operator turns it off, and round-trips a saved choice.
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
}
