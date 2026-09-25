extern alias UiAsm;

using CiRunLinks = UiAsm::Cockpit.Plugin.GitHubActions.Contracts.CiRunLinks;

namespace Cockpit.Plugin.GitHubActions.Tests;

// AC-1394: the browser-open URL guard, moved out of CiWorkflowRunClient into shared Contracts source since the UI
// part calls it directly (opening a browser needs no round trip through the channel). Aliased to the UI's own
// compiled copy (see the .csproj comment) — either copy behaves identically, this just picks one unambiguously.
public class CiRunLinksTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo/actions/runs/1", true)]
    [InlineData("https://api.github.com/x", true)]
    [InlineData("http://github.com/owner/repo", false)]      // not https
    [InlineData("https://github.com.evil.com/x", false)]     // look-alike host
    [InlineData("https://evil.com/github.com", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    public void IsGitHubRunUrl_AcceptsOnlyHttpsGitHub(string url, bool expected)
    {
        Assert.Equal(expected, CiRunLinks.IsGitHubRunUrl(url));
    }
}
