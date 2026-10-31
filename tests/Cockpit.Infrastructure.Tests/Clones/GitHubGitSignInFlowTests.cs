using Cockpit.Infrastructure.Clones;

namespace Cockpit.Infrastructure.Tests.Clones;

public sealed class GitHubGitSignInFlowTests
{
    // Layer 2 parsing: both gh platforms expose only the device code, never arbitrary output or a token.
    [Theory]
    [InlineData("One-time code (ABCD-EFGH) copied to clipboard")]
    [InlineData("! First copy your one-time code: ABCD-EFGH")]
    public void DeviceCodeIn_RecognizesGitHubInstructions_AndRejectsSecrets(string instruction)
    {
        Assert.Equal("ABCD-EFGH", GitHubGitSignInFlow.DeviceCodeIn("\u001b[33m" + instruction + "\u001b[0m"));
        Assert.Null(GitHubGitSignInFlow.DeviceCodeIn("token: github_pat_secretsecret"));
        Assert.Null(GitHubGitSignInFlow.DeviceCodeIn("arbitrary text ABCD-EFGH"));
        Assert.Null(GitHubGitSignInFlow.DeviceCodeIn("First copy your one-time code: ABCD-EFGHsecret"));
    }
}
