using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CodexMcpConfig"/> (#26/#44): turns the host-resolved MCP servers into <c>codex app-server</c>'s
/// <c>-c 'mcp_servers.&lt;name&gt;={…}'</c> overrides. The load-bearing property is that a bearer token never
/// lands in a config arg (visible in <c>/proc/&lt;pid&gt;/cmdline</c>) — it rides the process environment via
/// <c>bearer_token_env_var</c> instead.
/// </summary>
public class CodexMcpConfigTests
{
    [Fact]
    public void Build_WithNoServers_IsEmpty()
    {
        CodexMcpConfig.Build(null).Should().BeSameAs(CodexMcpLaunch.Empty);
        CodexMcpConfig.Build([]).Should().BeSameAs(CodexMcpLaunch.Empty);
    }

    [Fact]
    public void Build_ForAnHttpServerWithoutAToken_EmitsOnlyItsUrl()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "cockpit-orchestrator", Url = "http://127.0.0.1:8765/mcp" }]);

        launch.ConfigArgs.Should().Equal("-c", """mcp_servers.cockpit-orchestrator={ url = "http://127.0.0.1:8765/mcp" }""");
        launch.EnvironmentVariables.Should().BeEmpty();
    }

    [Fact]
    public void Build_ForACockpitHostedServer_ReferencesTheSharedAuthKeyEnvVar_AddingNothingToTheEnvironment()
    {
        // AC-40: a cockpit-hosted endpoint's auth is the host-set COCKPIT_MCP_KEY env var, so Codex points straight
        // at it and this builder emits no per-server env var of its own.
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:8765/mcp", CockpitHosted = true }]);

        launch.ConfigArgs.Should().Equal("-c", """mcp_servers.cockpit-session={ url = "http://127.0.0.1:8765/mcp", bearer_token_env_var = "COCKPIT_MCP_KEY" }""");
        launch.EnvironmentVariables.Should().BeEmpty();
    }

    [Fact]
    public void Build_ForAnHttpServerWithAToken_PutsTheTokenInTheEnvironment_NeverInTheArg()
    {
        const string token = "yt-pat-value";
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "youtrack", Url = "http://127.0.0.1:9000/mcp", BearerToken = token }]);

        // The arg carries only the env-var name, so the secret is not in the command line.
        launch.ConfigArgs.Should().Equal("-c", """mcp_servers.youtrack={ url = "http://127.0.0.1:9000/mcp", bearer_token_env_var = "COCKPIT_MCP_TOKEN_0" }""");
        launch.ConfigArgs.Should().NotContain(arg => arg.Contains(token));
        launch.EnvironmentVariables.Should().Contain(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_0", token));
    }

    [Fact]
    public void Build_IndexesTheTokenEnvVarPerServer_SoTwoTokensNeverCollide()
    {
        var launch = CodexMcpConfig.Build(
        [
            new PluginMcpServer { Name = "a", Url = "http://a/mcp", BearerToken = "token-a" },
            new PluginMcpServer { Name = "b", Url = "http://b/mcp", BearerToken = "token-b" },
        ]);

        launch.EnvironmentVariables.Should().Contain(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_0", "token-a"));
        launch.EnvironmentVariables.Should().Contain(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_1", "token-b"));
    }

    [Fact]
    public void Build_ForAStdioServer_EmitsCommandAndArgs()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "fs", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem"] }]);

        launch.ConfigArgs.Should().Equal("-c", """mcp_servers.fs={ command = "npx", args = ["-y", "@modelcontextprotocol/server-filesystem"] }""");
        launch.EnvironmentVariables.Should().BeEmpty();
    }

    [Fact]
    public void Build_QuotesAServerNameThatIsNotABareKey()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "my server", Url = "http://x/mcp" }]);

        launch.ConfigArgs[1].Should().StartWith("""mcp_servers."my server"=""");
    }

    [Fact]
    public void Build_EscapesQuotesAndBackslashesInValues_SoAValueCannotBreakTheToml()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "x", Url = """http://h/"a"\b""" }]);

        launch.ConfigArgs[1].Should().Be("""mcp_servers.x={ url = "http://h/\"a\"\\b" }""");
    }

    [Fact]
    public void Build_SkipsAServerWithNeitherUrlNorCommand()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "broken" }]);

        launch.ConfigArgs.Should().BeEmpty();
    }
}
