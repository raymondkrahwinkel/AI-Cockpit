using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// AC-1483: the lines `codex login status` printed on codex 0.154.0 (on stderr; the key masked by the CLI itself).
// Nothing here is read from auth.json; an unmeasured line, or one a warning only quotes, claims nothing.
public class CodexLoginStatusTests
{
    [Theory]
    [InlineData("Logged in using ChatGPT", PluginCredentialKind.RenewingLogin)]
    [InlineData("Logged in using an API key - ***", PluginCredentialKind.ApiKey)]
    [InlineData("Not logged in", PluginCredentialKind.Unknown)]
    [InlineData("Logged in using something else", PluginCredentialKind.Unknown)]
    [InlineData("", PluginCredentialKind.Unknown)]
    [InlineData("WARNING: proceeding, even though Logged in using ChatGPT was not confirmed\nNot logged in", PluginCredentialKind.Unknown)]
    [InlineData("WARNING: could not create PATH aliases\nLogged in using an API key - ***\n", PluginCredentialKind.ApiKey)]
    public void KindOf_ReadsTheStatusLine(string output, PluginCredentialKind expected) =>
        Assert.Equal(expected, CodexLoginStatus.KindOf(output));
}
