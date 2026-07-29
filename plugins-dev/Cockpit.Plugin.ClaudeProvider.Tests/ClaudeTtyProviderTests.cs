using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.TestSupport;

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
        var arguments = ClaudeTtyProvider.BuildArguments("plan", "opus", "high", mcpConfigPath: null, appendSystemPrompt: null, resume: null, settingsJson: null);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--permission-mode", "plan"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--model", "opus"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--effort", "high"));
    }

    [Fact]
    public void BuildArguments_Bypass_UsesDangerouslySkip_AndNotPermissionMode()
    {
        var arguments = ClaudeTtyProvider.BuildArguments("bypassPermissions", null, null, null, null, null, null);

        Assert.Contains("--dangerously-skip-permissions", arguments);
        Assert.DoesNotContain("--permission-mode", arguments);
    }

    [Fact]
    public void BuildArguments_ResumeMostRecent_IsContinue_BySessionId_IsResume()
    {
        Assert.Contains("--continue", ClaudeTtyProvider.BuildArguments(null, null, null, null, null, new PluginTtyResume(null), null));

        Assert.True(SequenceAssert.ContainsInOrder(
            ClaudeTtyProvider.BuildArguments(null, null, null, null, null, new PluginTtyResume("sess-1"), null),
            "--resume", "sess-1"));
    }

    [Fact]
    public void BuildArguments_McpConfig_Delegation_Settings_AreWired()
    {
        var arguments = ClaudeTtyProvider.BuildArguments(null, null, null, "/tmp/mcp.json", "delegate-prompt", null, "{\"statusLine\":{}}");

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--settings", "{\"statusLine\":{}}"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--mcp-config", "/tmp/mcp.json"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--append-system-prompt", "delegate-prompt"));
    }

    // AC-378: the strict flag is a deliberate divergence on the headless/SDK route only (ClaudeSdkArguments) — the
    // interactive TTY session the operator drives themselves keeps the union behaviour (cockpit servers add on top
    // of the CLI's own user/project claude.ai-connectors) unchanged. A regression here would silently strip the
    // operator's own connectors out of every interactive session.
    [Fact]
    public void BuildArguments_NeverAddsStrictMcpConfig_EvenWithAnMcpConfigPath()
    {
        ClaudeTtyProvider.BuildArguments(null, null, null, "/tmp/mcp.json", null, null, null)
            .Should().NotContain("--strict-mcp-config");
    }

    [Fact]
    public void BuildArguments_WithNothingSet_IsEmpty()
    {
        Assert.Empty(ClaudeTtyProvider.BuildArguments(null, null, null, null, null, null, null));
    }

    /// <summary>
    /// The standing instructions a profile/project gives a session (AC-142/AC-158) reach the interactive CLI, which
    /// is what a Claude profile starts as by default — they used to stop at the launch options, so the identity the
    /// operator typed was quietly dropped for every TTY session while the SDK route honoured it.
    /// </summary>
    [Fact]
    public void AppendedInstructions_CarryTheSessionsOwnInstructionsAheadOfTheOrchestratorNudge()
    {
        Assert.Equal("You are Olaf.\n\ndelegate-prompt", ClaudeTtyProvider._AppendedInstructions("You are Olaf.", "delegate-prompt"));

        Assert.Equal("You are Olaf.", ClaudeTtyProvider._AppendedInstructions("You are Olaf.", null));
        Assert.Equal("delegate-prompt", ClaudeTtyProvider._AppendedInstructions(null, "delegate-prompt"));
        Assert.Null(ClaudeTtyProvider._AppendedInstructions("   ", null));
    }
}
