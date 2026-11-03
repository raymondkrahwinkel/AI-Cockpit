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
    // AC-1150: a cockpit started as a stdio MCP server (a nested cockpit, or the dev app launched from an
    // agent session) has COCKPIT_MCP_KEY in its own process environment — the bearer for every loopback
    // endpoint (AC-1148). Build used to drop only the Anthropic family, so that bearer passed straight
    // through to the child. The unmarked variables inheriting are the positive control: without them, "the key
    // is scrubbed" would be indistinguishable from "everything is scrubbed".
    [Fact]
    public void Build_DropsTheCredentialAndTheCockpitMcpKey_ButKeepsWhatAToolServerNeeds()
    {
        var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/raymond",
            ["ANTHROPIC_API_KEY"] = "a-key-an-npx-server-has-no-business-with",
            ["COCKPIT_MCP_KEY"] = new string('a', 64),
        };

        var environment = StdioServerEnvironment.Build(inherited);

        Assert.DoesNotContain("ANTHROPIC_API_KEY", environment);
        Assert.DoesNotContain("COCKPIT_MCP_KEY", environment);
        Assert.Contains(new KeyValuePair<string, string?>("PATH", "/usr/bin"), environment);
        Assert.Contains(new KeyValuePair<string, string?>("HOME", "/home/raymond"), environment);
    }
}
