using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// The interactive-TUI command line is deliberately not the headless `exec --json` shape
// `CliSubprocessPluginSessionDriver` builds — these tests pin that difference (no `exec`, no
// `--json`, ever) against the real `codex --help`/`codex resume --help` flags rather than assumed.
public class CodexTtyProviderTests
{
    private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

    [Fact]
    public void BuildArguments_FreshSessionWithDefaultConfig_NeverAddsExecOrJson()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null);

        Assert.DoesNotContain("exec", arguments);
        Assert.DoesNotContain("--json", arguments);
    }

    [Fact]
    public void BuildArguments_FreshSession_HasNoResumeSubcommand()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null);

        Assert.DoesNotContain("resume", arguments);
    }

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

    [Fact]
    public void BuildArguments_NoModelConfiguredOrChosen_OmitsTheModelFlag()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(Model: null), NoOptions, resume: null);

        Assert.DoesNotContain("--model", arguments);
    }

    [Fact]
    public void BuildArguments_ModelOptionChosenInTheDialog_AddsTheModelFlag()
    {
        var options = new Dictionary<string, string> { [CodexTtyProvider.ModelOptionKey] = "o3" };

        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), options, resume: null);

        Assert.Contains("--model", arguments);
        Assert.Contains("o3", arguments);
    }

    [Fact]
    public void BuildEnvironmentOverlay_WithAConfigDir_SetsCodexHome()
    {
        var overlay = CodexTtyProvider.BuildEnvironmentOverlay(new CliAgentConfig(ConfigDir: "/home/raymond/.codex-work"));

        Assert.Equal("/home/raymond/.codex-work", overlay["CODEX_HOME"]);
    }

    [Fact]
    public void BuildEnvironmentOverlay_WithoutAConfigDir_IsEmpty()
    {
        var overlay = CodexTtyProvider.BuildEnvironmentOverlay(new CliAgentConfig(ConfigDir: null));

        Assert.Empty(overlay);
    }

    [Fact]
    public void BuildLaunch_ResolvesTheConfiguredCommandAndWorkingDirectoryFromTheContext()
    {
        var config = new CliAgentConfig(Command: "/usr/local/bin/codex", ConfigDir: "/home/raymond/.codex-work");
        var context = new PluginTtyLaunchContext(
            System.Text.Json.JsonSerializer.Serialize(config, CliAgentConfig.JsonOptions),
            NoOptions,
            WorkingDirectory: "/home/raymond/repo",
            Resume: null,
            BaseEnvironment: new Dictionary<string, string>());

        var spec = new CodexTtyProvider().BuildLaunch(context);

        Assert.Equal("/usr/local/bin/codex", spec.ExecutablePath);
        Assert.Equal("/home/raymond/repo", spec.WorkingDirectory);
        var overlay = spec.EnvironmentOverlay;
        Assert.Equal("/home/raymond/.codex-work", overlay["CODEX_HOME"]);
        Assert.Empty(spec.SessionScopedFiles);
    }

    [Fact]
    public void BuildLaunch_EmptyConfigJson_FallsBackToTheDefaultCommandInsteadOfThrowing()
    {
        var context = new PluginTtyLaunchContext(
            string.Empty,
            NoOptions,
            WorkingDirectory: "/home/raymond/repo",
            Resume: null,
            BaseEnvironment: new Dictionary<string, string>());

        var spec = new CodexTtyProvider().BuildLaunch(context);

        // Bare "codex" on a machine that has it installed resolves to the absolute path, and on one that does not
        // it stays bare for the OS to resolve at spawn time. Asserting either literal would be asserting the state
        // of the machine the test runs on — which is how this test failed the moment codex was installed here.
        Assert.Equal("codex", Path.GetFileNameWithoutExtension(spec.ExecutablePath));
    }

    // AC-77: the interactive TUI must receive the session's Cockpit MCP servers as `-c mcp_servers.*`
    // overrides, the same route the headless app-server takes — without this the Codex TUI only ever
    // sees its own ~/.codex servers.

    [Fact]
    public void BuildArguments_WithMcpConfigArgs_PrependsThemBeforeEverythingElse()
    {
        var mcpConfigArgs = new[] { "-c", """mcp_servers.youtrack={ url = "http://127.0.0.1:9000/mcp" }""" };

        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null, mcpConfigArgs);

        Assert.Equal(mcpConfigArgs, arguments.Take(mcpConfigArgs.Length));
    }

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
    public void BuildArguments_WithoutMcpConfigArgs_AddsNoConfigOverride()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null);

        Assert.DoesNotContain("-c", arguments);
    }

    [Fact]
    public void BuildLaunch_WithSessionMcpServers_FansThemIntoTheTuiAsConfigOverrides()
    {
        var context = _ContextWithServers(new PluginMcpServer { Name = "youtrack", Url = "http://127.0.0.1:9000/mcp", BearerToken = "yt-pat" });

        var spec = new CodexTtyProvider().BuildLaunch(context);

        Assert.Equal(
            new[] { "-c", """mcp_servers.youtrack={ url = "http://127.0.0.1:9000/mcp", bearer_token_env_var = "COCKPIT_MCP_TOKEN_0" }""" },
            spec.Arguments.Take(2));
        // The secret is in the environment, never in the command line.
        Assert.DoesNotContain(spec.Arguments, arg => arg.Contains("yt-pat"));
        Assert.Contains(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_0", "yt-pat"), spec.EnvironmentOverlay);
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

    [Fact]
    public void BuildLaunch_WithNoSessionMcpServers_AddsNoConfigOverride()
    {
        var context = _ContextWithServers();

        var spec = new CodexTtyProvider().BuildLaunch(context);

        Assert.DoesNotContain("-c", spec.Arguments);
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
