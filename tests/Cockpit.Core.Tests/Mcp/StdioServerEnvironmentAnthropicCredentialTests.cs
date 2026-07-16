using FluentAssertions;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The Anthropic credential never reaches a stdio MCP server the cockpit spawns. The old rule was "we
/// never set ANTHROPIC_API_KEY", which read as a guarantee but was not one: the child inherited whatever
/// the shell that launched the cockpit exported, silently moving billing onto that key. This pins the
/// difference between not setting a variable and not passing it on. The TTY-side equivalent
/// (<c>TtyEnvironment.BuildBase</c>) is covered in <c>Sessions.TtyEnvironmentTests</c>.
/// </summary>
public class StdioServerEnvironmentAnthropicCredentialTests
{
    [Fact]
    public void Build_DropsTheCredentialButKeepsWhatAToolServerNeeds()
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
