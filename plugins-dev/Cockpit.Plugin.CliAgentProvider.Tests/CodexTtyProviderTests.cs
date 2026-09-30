using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// The interactive-TUI command line is deliberately not the headless `exec --json` shape
// `CliSubprocessPluginSessionDriver` builds — these tests pin that difference (no `exec`, no
// `--json`, ever) against the real `codex --help`/`codex resume --help` flags rather than assumed.
public class CodexTtyProviderTests
{
    private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

    [Fact]
    public void BuildArguments_ResumeWithoutASessionId_UsesResumeLast()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, new PluginTtyResume(SessionId: null));

        Assert.Equal(new[] { "resume", "--last" }, arguments.Take(2));
    }

    [Fact]
    public void BuildArguments_ResumeWithASessionId_PassesItPositionally()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, new PluginTtyResume(SessionId: "thread-123"));

        Assert.Equal(new[] { "resume", "thread-123" }, arguments.Take(2));
    }

    [Fact]
    public void BuildArguments_DefaultConfig_IncludesTheConfiguredReadOnlySandboxDefault()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null);

        Assert.Contains("--sandbox", arguments);
        Assert.Contains("read-only", arguments);
    }

    [Fact]
    public void BuildArguments_SandboxOptionChosenInTheDialog_OverridesTheConfiguredDefault()
    {
        var config = new CliAgentConfig(SandboxMode: "read-only");
        var options = new Dictionary<string, string> { [CodexTtyProvider.SandboxOptionKey] = "workspace-write" };

        var arguments = CodexTtyProvider.BuildArguments(config, options, resume: null);

        Assert.Contains("--sandbox", arguments);
        Assert.Contains("workspace-write", arguments);
        Assert.DoesNotContain("read-only", arguments);
    }

    // AC-77: the interactive TUI must receive the session's Cockpit MCP servers as `-c mcp_servers.*`
    // overrides, the same route the headless app-server takes — without this the Codex TUI only ever
    // sees its own ~/.codex servers.

    [Fact]
    public void BuildArguments_WithMcpConfigArgsAndResume_PlacesTheConfigOverridesBeforeTheResumeSubcommand()
    {
        // Codex reads `-c` as a global flag taken before the subcommand; a `-c` after `resume` would not apply.
        var mcpConfigArgs = new[] { "-c", """mcp_servers.brain={ url = "http://127.0.0.1:9000/mcp" }""" };

        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, new PluginTtyResume(SessionId: "thread-123"), mcpConfigArgs);

        Assert.Equal(mcpConfigArgs, arguments.Take(mcpConfigArgs.Length));
        Assert.True(arguments.IndexOf("-c") < arguments.IndexOf("resume"));
    }

    [Fact]
    public void BuildLaunch_ForACockpitHostedServer_ReferencesTheSharedAuthKeyWithoutPuttingItInTheOverlay()
    {
        // COCKPIT_MCP_KEY is host-controlled and already on the base environment (TtyLauncher, AC-40); the provider
        // must only reference it, not set it — setting it in the overlay would be scrubbed and defeat the auth.
        var context = _ContextWithServers(new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:8765/mcp", CockpitHosted = true });

        var spec = new CodexTtyProvider().BuildLaunch(context);

        Assert.Contains("""mcp_servers.cockpit-session={ url = "http://127.0.0.1:8765/mcp", bearer_token_env_var = "COCKPIT_MCP_KEY" }""", spec.Arguments);
        Assert.DoesNotContain("COCKPIT_MCP_KEY", spec.EnvironmentOverlay);
    }

    private static PluginTtyLaunchContext _ContextWithServers(params PluginMcpServer[] servers) =>
        new(
            System.Text.Json.JsonSerializer.Serialize(new CliAgentConfig(), CliAgentConfig.JsonOptions),
            NoOptions,
            WorkingDirectory: "/home/raymond/repo",
            Resume: null,
            BaseEnvironment: new Dictionary<string, string>())
        {
            McpServers = servers,
        };
}
