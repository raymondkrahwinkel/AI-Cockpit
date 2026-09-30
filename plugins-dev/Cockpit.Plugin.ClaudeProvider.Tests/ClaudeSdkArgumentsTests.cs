using Cockpit.TestSupport;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeSdkArguments.BuildArguments` (Fase 4, SDK route) — the headless stream-json invocation, and the
// one thing it deliberately does NOT do that the host's in-tree spawn did: wire a `--permission-prompt-tool`/MCP
// permission server. Approvals ride the control protocol instead, so that flag must never appear.
public class ClaudeSdkArgumentsTests
{
    [Fact]
    public void BuildArguments_IsStreamingMode_WithoutPrint()
    {
        var arguments = ClaudeSdkArguments.BuildArguments(permissionMode: "default", model: null, resumeSessionId: null, continueMostRecent: false);

        // NO -p/--print: the in-band can_use_tool permission channel only fires in the SDK's streaming mode, matching
        // the official Agent SDK's own spawn. Adding -p routes permissions via --permission-prompt-tool and the CLI
        // never sends can_use_tool — proven ungated in a live run.
        Assert.DoesNotContain("-p", arguments);
        Assert.DoesNotContain("--print", arguments);
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--output-format", "stream-json"));
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--input-format", "stream-json"));
        Assert.Contains("--verbose", arguments);
        Assert.Contains("--include-partial-messages", arguments);
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--permission-mode", "default"));
    }

    [Fact]
    public void BuildArguments_WiresStdioPermissionPromptTool_ButNoMcpServer()
    {
        // The control-protocol route: --permission-prompt-tool stdio (what makes the CLI send can_use_tool over stdio),
        // but NONE of the HTTP MCP permission-server flags the in-tree route uses.
        var arguments = ClaudeSdkArguments.BuildArguments("default", "opus", null, false);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--permission-prompt-tool", "stdio"));
        Assert.DoesNotContain("--mcp-config", arguments);
        Assert.DoesNotContain("--strict-mcp-config", arguments);
    }

    [Fact]
    public void BuildArguments_FansTheMcpConfigWhenGiven_WithStrict_WhenUnattended()
    {
        // The user's own cockpit-configured servers (#26/#44) ride --mcp-config, so an SDK session actually reaches
        // them — dropping this is what left an SDK session with no registry servers. Strict (AC-378): an unattended
        // session must get EXACTLY the resolved servers, never the CLI's own user/project claude.ai-connectors
        // unioned in on top.
        var arguments = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, mcpConfigPath: "/tmp/cockpit-mcp/abc.json", strictMcpConfig: true);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--mcp-config", "/tmp/cockpit-mcp/abc.json"));
        Assert.Contains("--strict-mcp-config", arguments);
        // Still over the control protocol for approvals — the mcp-config is the user's servers, not a permission tool.
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--permission-prompt-tool", "stdio"));
    }

    [Fact]
    public void BuildArguments_Bypass_WiresNoPermissionPromptTool()
    {
        // Bypass allows everything with no prompt; wiring the stdio permission tool would re-introduce prompts.
        var arguments = ClaudeSdkArguments.BuildArguments("bypassPermissions", null, null, false);

        Assert.DoesNotContain("--permission-prompt-tool", arguments);
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--permission-mode", "bypassPermissions"));
    }

    [Fact]
    public void BuildArguments_BlankPermissionMode_DefaultsToDefault()
    {
        var arguments = ClaudeSdkArguments.BuildArguments(permissionMode: "  ", model: null, resumeSessionId: null, continueMostRecent: false);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--permission-mode", "default"));
    }

    // The defect this flag exists for (AC — assistant would not start on Windows): the appended system prompt is the
    // one argument with no ceiling — a standing instruction plus the operator's own memory files — and every platform
    // caps a command line (Windows 32.767 for the whole of it, Linux 131.072 for one argument). Measured on Windows
    // against the real CLI: 32.400 characters spawned, 32.876 failed at CreateProcess with no process and no stderr.
    // So the assertion that matters is not which flag is used but that the prompt's own size never reaches the
    // command line at all.
}
