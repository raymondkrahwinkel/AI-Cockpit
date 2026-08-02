using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// Custom headers (AC-354) on the Codex route. Codex offers two ways to spell them and only one of them is safe
// here: `http_headers` takes the value literally, which would put a credential in a `-c` argument, and a
// process argument is readable by every local account. `env_http_headers` maps the header to the *name* of an
// environment variable instead — the same rule the bearer token already follows.
public class CodexCustomHeaderTests
{
    private static PluginMcpServer _ServerWithHeader(string name, string value) => new()
    {
        Name = "private-api",
        Url = "https://api.example/mcp",
        Headers = new Dictionary<string, string> { [name] = value },
    };

    [Fact]
    public void Build_MapsACustomHeaderToAnEnvironmentVariable()
    {
        var launch = CodexMcpConfig.Build([_ServerWithHeader("X-Api-Key", "the-key")]);

        var config = string.Join(" ", launch.ConfigArgs);
        Assert.Contains("env_http_headers", config);
        Assert.Contains("\"X-Api-Key\" = \"COCKPIT_MCP_HEADER_0_0\"", config);
        Assert.Equal("the-key", launch.EnvironmentVariables["COCKPIT_MCP_HEADER_0_0"]);
    }

    [Fact]
    public void Build_NeverPutsTheHeaderValueOnTheCommandLine()
    {
        var launch = CodexMcpConfig.Build([_ServerWithHeader("X-Api-Key", "the-key")]);

        // The whole point of the env-var indirection. A -c argument shows up in /proc/<pid>/cmdline, so a literal
        // here would hand the credential to every local account on the machine.
        Assert.DoesNotContain("the-key", string.Join(" ", launch.ConfigArgs));
    }

    [Fact]
    public void Build_WithTwoServers_KeepsTheirHeaderVariablesApart()
    {
        var launch = CodexMcpConfig.Build([
            _ServerWithHeader("X-Api-Key", "first-key"),
            _ServerWithHeader("X-Api-Key", "second-key"),
        ]);

        Assert.Equal("first-key", launch.EnvironmentVariables["COCKPIT_MCP_HEADER_0_0"]);
        Assert.Equal("second-key", launch.EnvironmentVariables["COCKPIT_MCP_HEADER_1_0"]);
    }

    [Fact]
    public void Build_WithNoCustomHeaders_WritesNoHeaderField()
    {
        var launch = CodexMcpConfig.Build([new PluginMcpServer { Name = "plain", Url = "https://api.example/mcp" }]);

        Assert.DoesNotContain("env_http_headers", string.Join(" ", launch.ConfigArgs));
    }
}
