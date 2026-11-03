using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// Turns the host-resolved MCP servers into `codex app-server`'s `-c 'mcp_servers.&lt;name&gt;={…}'`
// overrides. The load-bearing property is that a bearer token never lands in a config arg
// (visible in `/proc/&lt;pid&gt;/cmdline`) — it rides the process environment instead.
public class CodexMcpConfigTests
{
    [Fact]
    public void Build_ForACockpitHostedServer_ReferencesTheSharedAuthKeyEnvVar_AddingNothingToTheEnvironment()
    {
        // AC-40: a cockpit-hosted endpoint's auth is the host-set COCKPIT_MCP_KEY env var, so Codex points straight
        // at it and this builder emits no per-server env var of its own.
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:8765/mcp", CockpitHosted = true }]);

        Assert.Equal(new[] { "-c", """mcp_servers.cockpit-session={ url = "http://127.0.0.1:8765/mcp", startup_timeout_sec = 60, bearer_token_env_var = "COCKPIT_MCP_KEY" }""" }, launch.ConfigArgs);
        Assert.Empty(launch.EnvironmentVariables);
    }

    [Fact]
    public void Build_ForAnHttpServerWithAToken_PutsTheTokenInTheEnvironment_NeverInTheArg()
    {
        const string token = "yt-pat-value";
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "youtrack", Url = "http://127.0.0.1:9000/mcp", BearerToken = token }]);

        // The arg carries only the env-var name, so the secret is not in the command line.
        Assert.Equal(new[] { "-c", """mcp_servers.youtrack={ url = "http://127.0.0.1:9000/mcp", startup_timeout_sec = 60, bearer_token_env_var = "COCKPIT_MCP_TOKEN_0" }""" }, launch.ConfigArgs);
        Assert.DoesNotContain(launch.ConfigArgs, arg => arg.Contains(token));
        Assert.Contains(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_0", token), launch.EnvironmentVariables);

        // The env var is indexed per server, so two tokens never collide.
        var two = CodexMcpConfig.Build(
        [
            new PluginMcpServer { Name = "a", Url = "http://a/mcp", BearerToken = "token-a" },
            new PluginMcpServer { Name = "b", Url = "http://b/mcp", BearerToken = "token-b" },
        ]);
        Assert.Contains(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_0", "token-a"), two.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_1", "token-b"), two.EnvironmentVariables);
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
