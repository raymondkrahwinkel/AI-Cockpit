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
    public void BuildArguments_FansTheMcpConfigWhenGiven_WithoutStrict_WhenAttended()
    {
        // The re-cut of AC-378: strictness belongs on "is anyone watching", not on "is this the SDK route". An
        // interactive SDK pane (SessionKind.Sdk, or a profile whose DefaultKind is Sdk) is a session the operator
        // drives themselves, so it unions with their own user/project config exactly like the TTY route — otherwise
        // opening a pane in SDK mode silently strips their claude.ai connectors.
        var arguments = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, mcpConfigPath: "/tmp/cockpit-mcp/abc.json");

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--mcp-config", "/tmp/cockpit-mcp/abc.json"));
        Assert.DoesNotContain("--strict-mcp-config", arguments);
    }

    [Fact]
    public void BuildArguments_OmitsMcpConfigAndStrict_WhenPathIsNullOrBlank()
    {
        // Both attended and unattended: a strict flag without a config to pair with would narrow to nothing by
        // accident, which is the trap in the other direction.
        var argsWithNullPath = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, mcpConfigPath: null, strictMcpConfig: true);
        Assert.DoesNotContain("--mcp-config", argsWithNullPath);
        Assert.DoesNotContain("--strict-mcp-config", argsWithNullPath);
        var argsWithBlankPath = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, mcpConfigPath: "   ", strictMcpConfig: true);
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
    public void BuildArguments_AppendsSystemPromptByPath_WhenGiven()
    {
        // The host folds an embedded run's hidden brief (Autopilot's CEO, AC-180) into the options map; the driver
        // resolves it, writes it, and hands the path here, so it must reach the CLI without a visible turn.
        var arguments = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, appendSystemPromptPath: "/tmp/cockpit-claude-prompt/abc.md");

        Assert.True(SequenceAssert.ContainsInOrder(arguments, "--append-system-prompt-file", "/tmp/cockpit-claude-prompt/abc.md"));
    }

    [Fact]
    public void BuildArguments_OmitsAppendSystemPrompt_WhenNullOrBlank()
    {
        Assert.DoesNotContain("--append-system-prompt-file", ClaudeSdkArguments.BuildArguments("default", "opus", null, false, appendSystemPromptPath: null));
        Assert.DoesNotContain("--append-system-prompt-file", ClaudeSdkArguments.BuildArguments("default", "opus", null, false, appendSystemPromptPath: "   "));
    }

    // The defect this flag exists for (AC — assistant would not start on Windows): the appended system prompt is the
    // one argument with no ceiling — a standing instruction plus the operator's own memory files — and every platform
    // caps a command line (Windows 32.767 for the whole of it, Linux 131.072 for one argument). Measured on Windows
    // against the real CLI: 32.400 characters spawned, 32.876 failed at CreateProcess with no process and no stderr.
    // So the assertion that matters is not which flag is used but that the prompt's own size never reaches the
    // command line at all.
    [Fact]
    public void BuildArguments_KeepsAHugeSystemPromptOffTheCommandLine()
    {
        var path = ClaudePrivateTempFile.WriteSystemPrompt(new string('x', 40_000))!;
        try
        {
            var arguments = ClaudeSdkArguments.BuildArguments("default", "opus", null, false, appendSystemPromptPath: path);

            Assert.Equal(new string('x', 40_000), File.ReadAllText(path));
            Assert.True(arguments.Sum(argument => argument.Length) < 4_000);
        }
        finally
        {
            ClaudePrivateTempFile.Delete(path);
        }
    }
}
