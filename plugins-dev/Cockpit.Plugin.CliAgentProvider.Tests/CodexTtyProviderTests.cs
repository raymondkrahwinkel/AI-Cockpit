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
}
