using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// `CodexMcpConfig` (#26/#44): turns the host-resolved MCP servers into `codex app-server`'s
// `-c 'mcp_servers.&lt;name&gt;={…}'` overrides. The load-bearing property is that a bearer token never
// lands in a config arg (visible in `/proc/&lt;pid&gt;/cmdline`) — it rides the process environment via
// `bearer_token_env_var` instead.
public class CodexMcpConfigTests
{
    [Fact]
    public void Build_WithNoServers_IsEmpty()
    {
        Assert.Same(CodexMcpLaunch.Empty, CodexMcpConfig.Build(null));
        Assert.Same(CodexMcpLaunch.Empty, CodexMcpConfig.Build([]));
    }

    [Fact]
    public void Build_ForAnHttpServerWithoutAToken_EmitsOnlyItsUrl()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "cockpit-orchestrator", Url = "http://127.0.0.1:8765/mcp" }]);

        Assert.Equal(new[] { "-c", """mcp_servers.cockpit-orchestrator={ url = "http://127.0.0.1:8765/mcp" }""" }, launch.ConfigArgs);
        Assert.Empty(launch.EnvironmentVariables);
    }

    [Fact]
    public void Build_ForACockpitHostedServer_ReferencesTheSharedAuthKeyEnvVar_AddingNothingToTheEnvironment()
    {
        // AC-40: a cockpit-hosted endpoint's auth is the host-set COCKPIT_MCP_KEY env var, so Codex points straight
        // at it and this builder emits no per-server env var of its own.
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:8765/mcp", CockpitHosted = true }]);

        Assert.Equal(new[] { "-c", """mcp_servers.cockpit-session={ url = "http://127.0.0.1:8765/mcp", bearer_token_env_var = "COCKPIT_MCP_KEY" }""" }, launch.ConfigArgs);
        Assert.Empty(launch.EnvironmentVariables);
    }

    [Fact]
    public void Build_ForAnHttpServerWithAToken_PutsTheTokenInTheEnvironment_NeverInTheArg()
    {
        const string token = "yt-pat-value";
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "youtrack", Url = "http://127.0.0.1:9000/mcp", BearerToken = token }]);

        // The arg carries only the env-var name, so the secret is not in the command line.
        Assert.Equal(new[] { "-c", """mcp_servers.youtrack={ url = "http://127.0.0.1:9000/mcp", bearer_token_env_var = "COCKPIT_MCP_TOKEN_0" }""" }, launch.ConfigArgs);
        Assert.DoesNotContain(launch.ConfigArgs, arg => arg.Contains(token));
        Assert.Contains(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_0", token), launch.EnvironmentVariables);
    }

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
    public void Build_ForAStdioServer_EmitsCommandAndArgs()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "fs", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem"] }]);

        Assert.Equal(new[] { "-c", """mcp_servers.fs={ command = "npx", args = ["-y", "@modelcontextprotocol/server-filesystem"] }""" }, launch.ConfigArgs);
        Assert.Empty(launch.EnvironmentVariables);
    }

    [Fact]
    public void Build_SanitizesAServerNameToCodexCharset_SoItStartsRatherThanBeingRejected()
    {
        // AC-77: Codex validates each server name against ^[a-zA-Z0-9_-]+$ and refuses "YouTrack: Personal" /
        // "SQL Explorer" with "Invalid MCP server name". Every out-of-charset character folds to '_', and the
        // result is a bare (unquoted) TOML key.
        var launch = CodexMcpConfig.Build(
        [
            new PluginMcpServer { Name = "YouTrack: Personal", Url = "http://x/mcp" },
            new PluginMcpServer { Name = "SQL Explorer", Url = "http://y/mcp" },
        ]);

        Assert.Equal(new[]
        {
            "-c", """mcp_servers.YouTrack__Personal={ url = "http://x/mcp" }""",
            "-c", """mcp_servers.SQL_Explorer={ url = "http://y/mcp" }""",
        }, launch.ConfigArgs);
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
            "-c", """mcp_servers.a_b={ url = "http://x/mcp" }""",
            "-c", """mcp_servers.a_b_2={ url = "http://y/mcp" }""",
        }, launch.ConfigArgs);
    }

    [Fact]
    public void Build_FallsBackToAnIndexedName_WhenAServerNameHasNoUsableCharacters()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "：（）", Url = "http://x/mcp" }]);

        Assert.Equal(new[] { "-c", """mcp_servers.server_0={ url = "http://x/mcp" }""" }, launch.ConfigArgs);
    }

    [Fact]
    public void Build_EscapesQuotesAndBackslashesInValues_SoAValueCannotBreakTheToml()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "x", Url = """http://h/"a"\b""" }]);

        Assert.Equal("""mcp_servers.x={ url = "http://h/\"a\"\\b" }""", launch.ConfigArgs[1]);
    }

    [Fact]
    public void Build_SkipsAServerWithNeitherUrlNorCommand()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "broken" }]);

        Assert.Empty(launch.ConfigArgs);
    }
}
