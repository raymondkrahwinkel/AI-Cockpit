namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Builds the <c>claude</c> CLI argument list for the SDK/session-driver route (Fase 4, weg A) — a port of the host's
/// <c>ClaudeCliProcess.BuildArguments</c> with one deliberate divergence in the <em>permission</em> wiring: no
/// host-owned permission MCP server. The plugin cannot reach the host's shared permission MCP server (weg A: the
/// plugin owns its own machinery), and it does not need to — spawning in bidirectional stream-json mode with
/// <c>--permission-prompt-tool stdio</c> makes the CLI route approvals back over the control protocol as
/// <c>can_use_tool</c> requests (<see cref="ClaudeControlProtocol"/>), exactly the way Codex's app-server route
/// surfaces its own in-band approvals. The user's own cockpit-configured MCP servers (#26/#44) <em>are</em> fanned in
/// via <c>--mcp-config</c> (without <c>--strict-mcp-config</c>) — that is orthogonal to the permission wiring, and
/// dropping it is what previously left an SDK session with no registry servers. Extracted and <c>internal</c> so the
/// flag construction is unit-testable without spawning a real process.
/// </summary>
internal static class ClaudeSdkArguments
{
    /// <summary>
    /// Sentinel value for <c>--permission-prompt-tool</c> that routes tool-approval prompts over the control protocol
    /// (as <c>can_use_tool</c> requests) rather than to an MCP server. Verified against the official Agent SDK, which
    /// sets exactly this when a <c>canUseTool</c> callback is provided (<c>client.py</c>:
    /// <c>replace(options, permission_prompt_tool_name="stdio")</c> — "Automatically set … to 'stdio' for control protocol").
    /// </summary>
    public const string StdioPermissionPromptTool = "stdio";

    /// <summary>
    /// The persistent, bidirectional <em>streaming</em> invocation — deliberately <b>without</b> <c>-p</c>/<c>--print</c>
    /// (the SDK uses "streaming mode with stdin"), <b>with</b> <c>--permission-prompt-tool stdio</c>. The two together are
    /// what make the CLI route tool approvals in-band as <c>can_use_tool</c> control_requests: without the stdio
    /// permission-prompt tool the CLI has no permission mechanism in headless mode and runs tools ungated (measured — a
    /// live run without it emitted zero <c>can_use_tool</c> requests). Bypass mode wires no permission tool, since it
    /// allows everything with no prompt. All grounded in the Agent SDK's own spawn (<c>subprocess_cli.py</c>/<c>client.py</c>).
    /// </summary>
    public static List<string> BuildArguments(
        string? permissionMode,
        string? model,
        string? resumeSessionId,
        bool continueMostRecent,
        string? appendSystemPrompt = null,
        string? mcpConfigPath = null)
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
        // Unlike the TTY route (ClaudeTtyProvider.BuildArguments), this one adds --strict-mcp-config (AC-378): a
        // headless/delegated session is unattended, so it must get EXACTLY the servers the resolution produced,
        // never the CLI's own user/project claude.ai-connectors (AI-OS-Brain, depot, Microsoft 365, …) unioned in
        // on top. Without the strict flag, narrowing a delegated task DOWN to fewer servers could leave it with
        // MORE (and different) ones than an unnarrowed task: a narrowing that resolves to nothing used to drop
        // --mcp-config entirely (see ClaudeSdkSessionDriver, which now always writes an explicit — possibly
        // empty — config file for this reason) and the session would inherit the full CLI config instead of
        // getting none. The interactive TTY route deliberately keeps the union behaviour — that is the session
        // the operator drives themselves, and Raymond does not want it to change.
        if (!string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            arguments.Add("--mcp-config");
            arguments.Add(mcpConfigPath);
            arguments.Add("--strict-mcp-config");
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        // A system prompt appended for this one session (AC-180): the host folds an embedded run's hidden brief (the
        // CEO's "you are the CEO, this is how you plan") into the options map under its well-known key, which the driver
        // resolves and hands here — the same --append-system-prompt channel the orchestrator nudge (#67) would ride.
        if (!string.IsNullOrWhiteSpace(appendSystemPrompt))
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(appendSystemPrompt);
        }

        return arguments;
    }
}
