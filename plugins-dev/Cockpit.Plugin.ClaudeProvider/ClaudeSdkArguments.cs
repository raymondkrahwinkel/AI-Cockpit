namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Builds the <c>claude</c> CLI argument list for the SDK/session-driver route (Fase 4, weg A) — a port of the host's
/// <c>ClaudeCliProcess.BuildArguments</c> with one deliberate divergence: <em>no</em> <c>--permission-prompt-tool</c>
/// /<c>--mcp-config</c>/<c>--strict-mcp-config</c> permission wiring. The plugin cannot reach the host's shared
/// permission MCP server (weg A: the plugin owns its own machinery), and it does not need to — spawning in
/// bidirectional stream-json mode without a permission-prompt tool makes the CLI route approvals back over the control
/// protocol as <c>can_use_tool</c> requests (<see cref="ClaudeControlProtocol"/>), exactly the way Codex's app-server
/// route surfaces its own in-band approvals. Extracted and <c>internal</c> so the flag construction is unit-testable
/// without spawning a real process.
/// </summary>
internal static class ClaudeSdkArguments
{
    /// <summary>
    /// The persistent, multi-turn headless invocation, per https://code.claude.com/docs/en/headless.md and
    /// https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode.md: <c>-p</c> with stream-json in/out,
    /// <c>--verbose</c> (required for stream-json output) and <c>--include-partial-messages</c> (token-level deltas).
    /// </summary>
    public static List<string> BuildArguments(
        string? permissionMode,
        string? model,
        string? resumeSessionId,
        bool continueMostRecent,
        string? delegationSystemPrompt = null)
    {
        var effectiveMode = string.IsNullOrWhiteSpace(permissionMode) ? "default" : permissionMode;

        var arguments = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--permission-mode", effectiveMode,
        };

        // Pick up an earlier conversation instead of starting cold — a named resume wins over "most recent". Both are
        // resolved by the CLI against its own history, so the cockpit never parses a transcript to hand the work back.
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            arguments.Add("--resume");
            arguments.Add(resumeSessionId.Trim());
        }
        else if (continueMostRecent)
        {
            arguments.Add("--continue");
        }

        // No permission-prompt-tool/mcp-config here — see the type remarks: approvals ride the control protocol.

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        // The orchestrator nudge (#67): its tools are only reached for if the model knows when they are worth it.
        if (!string.IsNullOrWhiteSpace(delegationSystemPrompt))
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(delegationSystemPrompt);
        }

        return arguments;
    }
}
