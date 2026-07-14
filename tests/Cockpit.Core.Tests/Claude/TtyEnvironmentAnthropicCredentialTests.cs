using FluentAssertions;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// The Anthropic credential never reaches a child the cockpit spawns. The old rule was "we never set
/// ANTHROPIC_API_KEY", which read as a guarantee but was not one: the child inherited whatever the shell that
/// launched the cockpit exported, silently moving the session off the operator's subscription onto API-key
/// billing. These pin the difference between not setting a variable and not passing it on.
/// </summary>
public class TtyEnvironmentAnthropicCredentialTests
{
    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("anthropic_api_key")]
    public void Build_DropsAnInheritedAnthropicCredential(string variable)
    {
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/bin",
            [variable] = "a-key-the-shell-exported",
        };

        var environment = TtyEnvironment.Build(inherited, profile: null, userProfileDirectory: "/home/raymond");

        environment.Should().NotContainKey(variable);
        environment.Should().ContainKey("PATH", "only the credential is dropped, not the rest of the environment");
    }

    [Fact]
    public void StdioServerEnvironment_DropsTheCredentialButKeepsWhatAToolServerNeeds()
    {
        var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/raymond",
            ["ANTHROPIC_API_KEY"] = "a-key-an-npx-server-has-no-business-with",
        };

        var environment = StdioServerEnvironment.Build(inherited);

        environment.Should().NotContainKey("ANTHROPIC_API_KEY");
        environment.Should().Contain("PATH", "/usr/bin");
        environment.Should().Contain("HOME", "/home/raymond");
    }
}
