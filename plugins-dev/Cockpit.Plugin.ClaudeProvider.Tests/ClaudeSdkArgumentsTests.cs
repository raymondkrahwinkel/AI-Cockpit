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

    // The defect this flag exists for (AC — assistant would not start on Windows): the appended system prompt is the
    // one argument with no ceiling — a standing instruction plus the operator's own memory files — and every platform
    // caps a command line (Windows 32.767 for the whole of it, Linux 131.072 for one argument). Measured on Windows
    // against the real CLI: 32.400 characters spawned, 32.876 failed at CreateProcess with no process and no stderr.
    // So the assertion that matters is not which flag is used but that the prompt's own size never reaches the
    // command line at all.
}
