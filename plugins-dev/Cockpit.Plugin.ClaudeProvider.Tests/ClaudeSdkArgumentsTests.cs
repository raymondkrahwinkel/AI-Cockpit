using Cockpit.TestSupport;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeSdkArguments.BuildArguments"/> (Fase 4, SDK route) — the headless stream-json invocation, and the
/// one thing it deliberately does NOT do that the host's in-tree spawn did: wire a <c>--permission-prompt-tool</c>/MCP
/// permission server. Approvals ride the control protocol instead, so that flag must never appear.
/// </summary>
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
    public void BuildArguments_FansTheMcpConfigWhenGiven_WithStrict()
    {
        // The user's own cockpit-configured servers (#26/#44) ride --mcp-config, so an SDK session actually reaches
        // them — dropping this is what left an SDK session with no registry servers. Strict (AC-378, unlike the TTY
        // route): a headless/delegated session must get EXACTLY the resolved servers, never the CLI's own
        // user/project claude.ai-connectors unioned in on top.
        var arguments = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, mcpConfigPath: "/tmp/cockpit-mcp/abc.json");

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--mcp-config", "/tmp/cockpit-mcp/abc.json"));
        Assert.Contains("--strict-mcp-config", arguments);
        // Still over the control protocol for approvals — the mcp-config is the user's servers, not a permission tool.
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--permission-prompt-tool", "stdio"));
    }

    [Fact]
    public void BuildArguments_OmitsMcpConfigAndStrict_WhenPathIsNullOrBlank()
    {
        var argsWithNullPath = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, mcpConfigPath: null);
        Assert.DoesNotContain("--mcp-config", argsWithNullPath);
        Assert.DoesNotContain("--strict-mcp-config", argsWithNullPath);
        var argsWithBlankPath = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, mcpConfigPath: "   ");
        Assert.DoesNotContain("--mcp-config", argsWithBlankPath);
        Assert.DoesNotContain("--strict-mcp-config", argsWithBlankPath);
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
    public void BuildArguments_ModelAndNamedResume_AreFlags()
    {
        var arguments = ClaudeSdkArguments.BuildArguments("plan", "sonnet", resumeSessionId: "sess-123", continueMostRecent: true);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--model", "sonnet"));
        // A named resume wins over "most recent": --resume with the id, never --continue.
        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--resume", "sess-123"));
        Assert.DoesNotContain("--continue", arguments);
    }

    [Fact]
    public void BuildArguments_ContinueMostRecent_WhenNoNamedResume()
    {
        var arguments = ClaudeSdkArguments.BuildArguments("default", null, resumeSessionId: null, continueMostRecent: true);

        Assert.Contains("--continue", arguments);
        Assert.DoesNotContain("--resume", arguments);
    }

    [Fact]
    public void BuildArguments_BlankPermissionMode_DefaultsToDefault()
    {
        var arguments = ClaudeSdkArguments.BuildArguments(permissionMode: "  ", model: null, resumeSessionId: null, continueMostRecent: false);

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--permission-mode", "default"));
    }

    [Fact]
    public void BuildArguments_AppendsSystemPrompt_WhenGiven()
    {
        // The host folds an embedded run's hidden brief (Autopilot's CEO, AC-180) into the options map; the driver
        // resolves it and hands it here, so it must reach the CLI as --append-system-prompt without a visible turn.
        var arguments = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, appendSystemPrompt: "You are the CEO.");

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--append-system-prompt", "You are the CEO."));
    }

    [Fact]
    public void BuildArguments_OmitsAppendSystemPrompt_WhenNullOrBlank()
    {
        Assert.DoesNotContain("--append-system-prompt", ClaudeSdkArguments.BuildArguments("default", "opus", null, false, appendSystemPrompt: null));
        Assert.DoesNotContain("--append-system-prompt", ClaudeSdkArguments.BuildArguments("default", "opus", null, false, appendSystemPrompt: "   "));
    }
}
