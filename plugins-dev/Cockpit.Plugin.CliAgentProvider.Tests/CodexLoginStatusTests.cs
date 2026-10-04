using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// AC-1483: the lines `codex login status` printed on codex 0.154.0 (the key masked by the CLI itself). Nothing here
// is read from auth.json; an unmeasured line claims nothing.
public class CodexLoginStatusTests
{
    [Theory]
    [InlineData("Logged in using ChatGPT", PluginCredentialKind.RenewingLogin)]
    [InlineData("Logged in using an API key - ***", PluginCredentialKind.ApiKey)]
    [InlineData("Not logged in", PluginCredentialKind.Unknown)]
    [InlineData("Logged in using something else", PluginCredentialKind.Unknown)]
    [InlineData("", PluginCredentialKind.Unknown)]
    public void KindOf_ReadsTheStatusLine(string line, PluginCredentialKind expected) =>
        Assert.Equal(expected, CodexLoginStatus.KindOf(line));
}
