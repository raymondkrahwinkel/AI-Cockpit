using Cockpit.TestSupport;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeTtyProvider.BuildArguments` (Fase 4) — the launch-only flag composition ported from the host's
// in-tree Claude TTY provider, proven without a real pty: the same mode/model/effort/resume/mcp/delegation wiring,
// and bypass as the launch-only synonym for --dangerously-skip-permissions.
public class ClaudeTtyProviderTests
{
    [Fact]
    public void BuildArguments_Bypass_UsesDangerouslySkip_AndNotPermissionMode()
    {
        var arguments = ClaudeTtyProvider.BuildArguments("bypassPermissions", null, null, null, null, null, null);

        Assert.Contains("--dangerously-skip-permissions", arguments);
        Assert.DoesNotContain("--permission-mode", arguments);
    }

    [Fact]
    public void BuildArguments_McpConfig_Delegation_Settings_AreWired()
    {
        var arguments = ClaudeTtyProvider.BuildArguments(null, null, null, "/tmp/mcp.json", "/tmp/prompt.md", null, "{\"statusLine\":{}}");

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--settings", "{\"statusLine\":{}}"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--mcp-config", "/tmp/mcp.json"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--append-system-prompt-file", "/tmp/prompt.md"));
    }

    // AC-378: the strict flag is a deliberate divergence on the headless/SDK route only (ClaudeSdkArguments) — the
    // interactive TTY session the operator drives themselves keeps the union behaviour (cockpit servers add on top
    // of the CLI's own user/project claude.ai-connectors) unchanged. A regression here would silently strip the
    // operator's own connectors out of every interactive session.

    // The standing instructions a profile/project gives a session (AC-142/AC-158) reach the interactive CLI, which
    // is what a Claude profile starts as by default — they used to stop at the launch options, so the identity the
    // operator typed was quietly dropped for every TTY session while the SDK route honoured it.
}
