namespace Cockpit.Plugin.ClaudeProvider;

// Builds the `claude` CLI argument list for the SDK/session-driver route (Fase 4, weg A) — a port of the host's
// `ClaudeCliProcess.BuildArguments` with one deliberate divergence in the *permission* wiring: no
// host-owned permission MCP server. The plugin cannot reach the host's shared permission MCP server (weg A: the
// plugin owns its own machinery), and it does not need to — spawning in bidirectional stream-json mode with
// `--permission-prompt-tool stdio` makes the CLI route approvals back over the control protocol as
// `can_use_tool` requests (`ClaudeControlProtocol`), exactly the way Codex's app-server route
// surfaces its own in-band approvals. The user's own cockpit-configured MCP servers (#26/#44) *are* fanned in
// via `--mcp-config` (paired with `--strict-mcp-config` only when the session is unattended) — that is
// orthogonal to the permission wiring, and
// dropping it is what previously left an SDK session with no registry servers. Extracted and `internal` so the
// flag construction is unit-testable without spawning a real process.
internal static class ClaudeSdkArguments
{
    // Sentinel value for `--permission-prompt-tool` that routes tool-approval prompts over the control protocol
    // (as `can_use_tool` requests) rather than to an MCP server. Verified against the official Agent SDK, which
    // sets exactly this when a `canUseTool` callback is provided (`client.py`:
    // `replace(options, permission_prompt_tool_name="stdio")` — "Automatically set … to 'stdio' for control protocol").
    public const string StdioPermissionPromptTool = "stdio";

    // AC-1058: told to the model itself (via the unattended session's own append-system-prompt) so a skill that
    // names an unmounted plugin tool is not followed blind. Kept beside the strict-mcp-config comment below so the
    // explanation and the text handed to the model cannot drift apart.
    public const string UnattendedPluginMcpNotice =
        "This is an unattended Claude Code session: MCP servers belonging to Claude Code's own plugins are not " +
        "mounted, even though their skills, hooks, and slash commands still load. A skill that names an " +
        "mcp__plugin_*__ tool cannot be followed here — use the CLI's built-in tools instead. This is deliberate " +
        "containment for a session nobody is watching, not a malfunction.";

    // The persistent, bidirectional *streaming* invocation — deliberately *without* `-p`/`--print`
    // (the SDK uses "streaming mode with stdin"), *with* `--permission-prompt-tool stdio`. The two together are
    // what make the CLI route tool approvals in-band as `can_use_tool` control_requests: without the stdio
    // permission-prompt tool the CLI has no permission mechanism in headless mode and runs tools ungated (measured — a
    // live run without it emitted zero `can_use_tool` requests). Bypass mode wires no permission tool, since it
    // allows everything with no prompt. All grounded in the Agent SDK's own spawn (`subprocess_cli.py`/`client.py`).
    public static List<string> BuildArguments(
        string? permissionMode,
        string? model,
        string? resumeSessionId,
        bool continueMostRecent,
        string? appendSystemPromptPath = null,
        string? mcpConfigPath = null,
        bool strictMcpConfig = false)
    {
        var effectiveMode = string.IsNullOrWhiteSpace(permissionMode) ? "default" : permissionMode;

        var arguments = new List<string>
        {
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--input-format", "stream-json",
            "--permission-mode", effectiveMode,
        };

        // Route approvals over the control protocol (can_use_tool) — but not in bypass, which allows everything with no
        // prompt and where wiring a permission tool would re-introduce the very prompts bypass asked to skip.
        if (!string.Equals(effectiveMode, "bypassPermissions", StringComparison.Ordinal))
        {
            arguments.Add("--permission-prompt-tool");
            arguments.Add(StdioPermissionPromptTool);
        }

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

        // Fan the shared MCP registry into the SDK spawn — the user's own cockpit-configured servers (#26/#44).
        // --strict-mcp-config (AC-378, unattended only) makes --mcp-config authoritative — CLI: "ignoring all other
        // MCP configurations" — so it also drops Claude Code's own plugin MCP servers; their skills/hooks/slash-
        // commands still load (AC-1058). Want one plugin server anyway? Register it as plugin_<plugin>_<server>.
        //
        // What AC-378 first hung on "is this the SDK route" belongs on "is anyone watching": the operator can open
        // an interactive SDK pane from the New-session dialog (SessionKind.Sdk) or through a profile whose
        // DefaultKind is Sdk, and that pane is a session Raymond drives himself. Strict there stripped his own
        // account connectors out from under him — precisely the regression ClaudeTtyProviderTests:52 describes for
        // the TTY route. So an attended SDK session now unions like the TTY one, and the strict guarantee stays
        // where the escalation it prevents can actually happen: where an agent, not a person, asks for the rights.
        if (!string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            arguments.Add("--mcp-config");
            arguments.Add(mcpConfigPath);
            if (strictMcpConfig)
            {
                arguments.Add("--strict-mcp-config");
            }
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        // A system prompt appended for this one session (AC-180): the host folds an embedded run's hidden brief (the
        // CEO's "you are the CEO, this is how you plan") into the options map under its well-known key, which the driver
        // resolves and hands here — the same channel the orchestrator nudge (#67) would ride.
        //
        // By path, never by value. This is the one argument with no ceiling on it (the assistant's is the standing
        // instruction plus the operator's own memory files), and a command line has one on every platform — see
        // `ClaudePrivateTempFile.WriteSystemPrompt`, which owns that reasoning and writes the file the driver hands
        // in here.
        if (!string.IsNullOrWhiteSpace(appendSystemPromptPath))
        {
            arguments.Add("--append-system-prompt-file");
            arguments.Add(appendSystemPromptPath);
        }

        return arguments;
    }
}
