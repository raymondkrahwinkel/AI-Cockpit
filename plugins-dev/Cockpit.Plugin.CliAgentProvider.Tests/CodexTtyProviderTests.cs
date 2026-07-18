using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CodexTtyProvider"/> (#45 fase B2): the interactive-TUI command line is deliberately not the
/// headless <c>exec --json</c> shape <see cref="CliSubprocessPluginSessionDriver"/> builds — these tests
/// pin exactly that difference (no <c>exec</c>, no <c>--json</c>, ever) plus resume/sandbox/model, all
/// checked against the real <c>codex --help</c>/<c>codex resume --help</c> flags rather than assumed.
/// </summary>
public class CodexTtyProviderTests
{
    private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

    [Fact]
    public void BuildArguments_FreshSessionWithDefaultConfig_NeverAddsExecOrJson()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null);

        arguments.Should().NotContain("exec");
        arguments.Should().NotContain("--json");
    }

    [Fact]
    public void BuildArguments_FreshSession_HasNoResumeSubcommand()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null);

        arguments.Should().NotContain("resume");
    }

    [Fact]
    public void BuildArguments_ResumeWithoutASessionId_UsesResumeLast()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, new PluginTtyResume(SessionId: null));

        arguments.Should().StartWith(["resume", "--last"]);
    }

    [Fact]
    public void BuildArguments_ResumeWithASessionId_PassesItPositionally()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, new PluginTtyResume(SessionId: "thread-123"));

        arguments.Should().StartWith(["resume", "thread-123"]);
    }

    [Fact]
    public void BuildArguments_DefaultConfig_IncludesTheConfiguredReadOnlySandboxDefault()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null);

        arguments.Should().Contain(["--sandbox", "read-only"]);
    }

    [Fact]
    public void BuildArguments_SandboxOptionChosenInTheDialog_OverridesTheConfiguredDefault()
    {
        var config = new CliAgentConfig(SandboxMode: "read-only");
        var options = new Dictionary<string, string> { [CodexTtyProvider.SandboxOptionKey] = "workspace-write" };

        var arguments = CodexTtyProvider.BuildArguments(config, options, resume: null);

        arguments.Should().Contain(["--sandbox", "workspace-write"]);
        arguments.Should().NotContain("read-only");
    }

    [Fact]
    public void BuildArguments_NoModelConfiguredOrChosen_OmitsTheModelFlag()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(Model: null), NoOptions, resume: null);

        arguments.Should().NotContain("--model");
    }

    [Fact]
    public void BuildArguments_ModelOptionChosenInTheDialog_AddsTheModelFlag()
    {
        var options = new Dictionary<string, string> { [CodexTtyProvider.ModelOptionKey] = "o3" };

        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), options, resume: null);

        arguments.Should().Contain(["--model", "o3"]);
    }

    [Fact]
    public void BuildEnvironmentOverlay_WithAConfigDir_SetsCodexHome()
    {
        var overlay = CodexTtyProvider.BuildEnvironmentOverlay(new CliAgentConfig(ConfigDir: "/home/raymond/.codex-work"));

        overlay.Should().ContainKey("CODEX_HOME").WhoseValue.Should().Be("/home/raymond/.codex-work");
    }

    [Fact]
    public void BuildEnvironmentOverlay_WithoutAConfigDir_IsEmpty()
    {
        var overlay = CodexTtyProvider.BuildEnvironmentOverlay(new CliAgentConfig(ConfigDir: null));

        overlay.Should().BeEmpty();
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

        spec.ExecutablePath.Should().Be("/usr/local/bin/codex");
        spec.WorkingDirectory.Should().Be("/home/raymond/repo");
        spec.EnvironmentOverlay.Should().ContainKey("CODEX_HOME").WhoseValue.Should().Be("/home/raymond/.codex-work");
        spec.SessionScopedFiles.Should().BeEmpty();
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
        Path.GetFileNameWithoutExtension(spec.ExecutablePath).Should().Be("codex");
    }

    // AC-77: the interactive TUI must receive the session's Cockpit MCP servers as `-c mcp_servers.*` overrides,
    // the same route the headless app-server takes — without this the Codex TUI only ever sees its own ~/.codex
    // servers. These pin that the overrides are present, precede any `resume`, and that a bearer token rides the
    // environment rather than the command line.

    [Fact]
    public void BuildArguments_WithMcpConfigArgs_PrependsThemBeforeEverythingElse()
    {
        var mcpConfigArgs = new[] { "-c", """mcp_servers.youtrack={ url = "http://127.0.0.1:9000/mcp" }""" };

        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null, mcpConfigArgs);

        arguments.Should().StartWith(mcpConfigArgs);
    }

    [Fact]
    public void BuildArguments_WithMcpConfigArgsAndResume_PlacesTheConfigOverridesBeforeTheResumeSubcommand()
    {
        // Codex reads `-c` as a global flag taken before the subcommand; a `-c` after `resume` would not apply.
        var mcpConfigArgs = new[] { "-c", """mcp_servers.brain={ url = "http://127.0.0.1:9000/mcp" }""" };

        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, new PluginTtyResume(SessionId: "thread-123"), mcpConfigArgs);

        arguments.Should().StartWith(mcpConfigArgs);
        arguments.IndexOf("-c").Should().BeLessThan(arguments.IndexOf("resume"));
    }

    [Fact]
    public void BuildArguments_WithoutMcpConfigArgs_AddsNoConfigOverride()
    {
        var arguments = CodexTtyProvider.BuildArguments(new CliAgentConfig(), NoOptions, resume: null);

        arguments.Should().NotContain("-c");
    }

    [Fact]
    public void BuildLaunch_WithSessionMcpServers_FansThemIntoTheTuiAsConfigOverrides()
    {
        var context = _ContextWithServers(new PluginMcpServer { Name = "youtrack", Url = "http://127.0.0.1:9000/mcp", BearerToken = "yt-pat" });

        var spec = new CodexTtyProvider().BuildLaunch(context);

        spec.Arguments.Should().StartWith(["-c", """mcp_servers.youtrack={ url = "http://127.0.0.1:9000/mcp", bearer_token_env_var = "COCKPIT_MCP_TOKEN_0" }"""]);
        // The secret is in the environment, never in the command line.
        spec.Arguments.Should().NotContain(arg => arg.Contains("yt-pat"));
        spec.EnvironmentOverlay.Should().Contain(new KeyValuePair<string, string?>("COCKPIT_MCP_TOKEN_0", "yt-pat"));
    }

    [Fact]
    public void BuildLaunch_ForACockpitHostedServer_ReferencesTheSharedAuthKeyWithoutPuttingItInTheOverlay()
    {
        // COCKPIT_MCP_KEY is host-controlled and already on the base environment (TtyLauncher, AC-40); the provider
        // must only reference it, not set it — setting it in the overlay would be scrubbed and defeat the auth.
        var context = _ContextWithServers(new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:8765/mcp", CockpitHosted = true });

        var spec = new CodexTtyProvider().BuildLaunch(context);

        spec.Arguments.Should().Contain("""mcp_servers.cockpit-session={ url = "http://127.0.0.1:8765/mcp", bearer_token_env_var = "COCKPIT_MCP_KEY" }""");
        spec.EnvironmentOverlay.Should().NotContainKey("COCKPIT_MCP_KEY");
    }

    [Fact]
    public void BuildLaunch_WithNoSessionMcpServers_AddsNoConfigOverride()
    {
        var context = _ContextWithServers();

        var spec = new CodexTtyProvider().BuildLaunch(context);

        spec.Arguments.Should().NotContain("-c");
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
