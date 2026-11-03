using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// Turns the host-resolved MCP servers into `codex app-server`'s `-c 'mcp_servers.&lt;name&gt;={…}'`
// overrides. The load-bearing property is that a bearer token never lands in a config arg
// (visible in `/proc/&lt;pid&gt;/cmdline`) — it rides the process environment instead.
public class CodexMcpConfigTests
{

    [Fact]
    public void Build_IndexesTheTokenEnvVarPerServer_SoTwoTokensNeverCollide()
    {
        var launch = CodexMcpConfig.Build(
        [
            new PluginMcpServer { Name = "a", Url = "http://a/mcp", BearerToken = "token-a" },
            new PluginMcpServer { Name = "b", Url = "http://b/mcp", BearerToken = "token-b" },
        ]);

        Assert.Contains(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_0", "token-a"), launch.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_1", "token-b"), launch.EnvironmentVariables);
    }

    [Fact]
    public void Build_MakesSanitizedNamesUnique_SoTwoNamesThatFoldTheSameDoNotCollapseIntoOneServer()
    {
        var launch = CodexMcpConfig.Build(
        [
            new PluginMcpServer { Name = "a b", Url = "http://x/mcp" },
            new PluginMcpServer { Name = "a:b", Url = "http://y/mcp" },
        ]);

        Assert.Equal(new[]
        {
            "-c", """mcp_servers.a_b={ url = "http://x/mcp", startup_timeout_sec = 60 }""",
            "-c", """mcp_servers.a_b_2={ url = "http://y/mcp", startup_timeout_sec = 60 }""",
        }, launch.ConfigArgs);
    }

    [Fact]
    public void Build_EscapesQuotesAndBackslashesInValues_SoAValueCannotBreakTheToml()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "x", Url = """http://h/"a"\b""" }]);

        Assert.Equal("""mcp_servers.x={ url = "http://h/\"a\"\\b", startup_timeout_sec = 60 }""", launch.ConfigArgs[1]);
    }
}
