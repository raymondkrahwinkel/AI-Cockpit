using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeTtyProvider.BuildArguments"/> (Fase 4) — the launch-only flag composition ported from the host's
/// in-tree Claude TTY provider, proven without a real pty: the same mode/model/effort/resume/mcp/delegation wiring,
/// and bypass as the launch-only synonym for --dangerously-skip-permissions.
/// </summary>
public class ClaudeTtyProviderTests
{
    [Fact]
    public void BuildArguments_PermissionModeModelEffort_AreFlags()
    {
        var arguments = ClaudeTtyProvider.BuildArguments("plan", "opus", "high", mcpConfigPath: null, delegationSystemPrompt: null, resume: null, settingsJson: null);

        arguments.Should().ContainInOrder("--permission-mode", "plan");
        arguments.Should().ContainInOrder("--model", "opus");
        arguments.Should().ContainInOrder("--effort", "high");
    }

    [Fact]
    public void BuildArguments_Bypass_UsesDangerouslySkip_AndNotPermissionMode()
    {
        var arguments = ClaudeTtyProvider.BuildArguments("bypassPermissions", null, null, null, null, null, null);

        arguments.Should().Contain("--dangerously-skip-permissions");
        arguments.Should().NotContain("--permission-mode");
    }

    [Fact]
    public void BuildArguments_ResumeMostRecent_IsContinue_BySessionId_IsResume()
    {
        ClaudeTtyProvider.BuildArguments(null, null, null, null, null, new PluginTtyResume(null), null)
            .Should().Contain("--continue");

        ClaudeTtyProvider.BuildArguments(null, null, null, null, null, new PluginTtyResume("sess-1"), null)
            .Should().ContainInOrder("--resume", "sess-1");
    }

    [Fact]
    public void BuildArguments_McpConfig_Delegation_Settings_AreWired()
    {
        var arguments = ClaudeTtyProvider.BuildArguments(null, null, null, "/tmp/mcp.json", "delegate-prompt", null, "{\"statusLine\":{}}");

        arguments.Should().ContainInOrder("--settings", "{\"statusLine\":{}}");
        arguments.Should().ContainInOrder("--mcp-config", "/tmp/mcp.json");
        arguments.Should().ContainInOrder("--append-system-prompt", "delegate-prompt");
    }

    [Fact]
    public void BuildArguments_WithNothingSet_IsEmpty()
    {
        ClaudeTtyProvider.BuildArguments(null, null, null, null, null, null, null).Should().BeEmpty();
    }
}
