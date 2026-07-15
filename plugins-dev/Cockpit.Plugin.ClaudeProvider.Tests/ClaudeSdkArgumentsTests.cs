using FluentAssertions;

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
        arguments.Should().NotContain("-p");
        arguments.Should().NotContain("--print");
        arguments.Should().ContainInOrder("--output-format", "stream-json");
        arguments.Should().ContainInOrder("--input-format", "stream-json");
        arguments.Should().Contain("--verbose");
        arguments.Should().Contain("--include-partial-messages");
        arguments.Should().ContainInOrder("--permission-mode", "default");
    }

    [Fact]
    public void BuildArguments_WiresStdioPermissionPromptTool_ButNoMcpServer()
    {
        // The control-protocol route: --permission-prompt-tool stdio (what makes the CLI send can_use_tool over stdio),
        // but NONE of the HTTP MCP permission-server flags the in-tree route uses.
        var arguments = ClaudeSdkArguments.BuildArguments("default", "opus", null, false);

        arguments.Should().ContainInOrder("--permission-prompt-tool", "stdio");
        arguments.Should().NotContain("--mcp-config");
        arguments.Should().NotContain("--strict-mcp-config");
    }

    [Fact]
    public void BuildArguments_Bypass_WiresNoPermissionPromptTool()
    {
        // Bypass allows everything with no prompt; wiring the stdio permission tool would re-introduce prompts.
        var arguments = ClaudeSdkArguments.BuildArguments("bypassPermissions", null, null, false);

        arguments.Should().NotContain("--permission-prompt-tool");
        arguments.Should().ContainInOrder("--permission-mode", "bypassPermissions");
    }

    [Fact]
    public void BuildArguments_ModelAndNamedResume_AreFlags()
    {
        var arguments = ClaudeSdkArguments.BuildArguments("plan", "sonnet", resumeSessionId: "sess-123", continueMostRecent: true);

        arguments.Should().ContainInOrder("--model", "sonnet");
        // A named resume wins over "most recent": --resume with the id, never --continue.
        arguments.Should().ContainInOrder("--resume", "sess-123");
        arguments.Should().NotContain("--continue");
    }

    [Fact]
    public void BuildArguments_ContinueMostRecent_WhenNoNamedResume()
    {
        var arguments = ClaudeSdkArguments.BuildArguments("default", null, resumeSessionId: null, continueMostRecent: true);

        arguments.Should().Contain("--continue");
        arguments.Should().NotContain("--resume");
    }

    [Fact]
    public void BuildArguments_BlankPermissionMode_DefaultsToDefault()
    {
        var arguments = ClaudeSdkArguments.BuildArguments(permissionMode: "  ", model: null, resumeSessionId: null, continueMostRecent: false);

        arguments.Should().ContainInOrder("--permission-mode", "default");
    }
}
