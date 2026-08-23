namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Option keys the host bridges from its own typed session-start surface into a plugin driver's
/// <see cref="IPluginSessionDriver.StartAsync(string?, string?, string?, System.Collections.Generic.IReadOnlyDictionary{string, string}?, System.Collections.Generic.IReadOnlyList{PluginMcpServer}?, System.Threading.CancellationToken)"/>
/// options map.
/// </summary>
/// <remarks>
/// A provider that does not declare a given key never reads it — carrying an unread key is always safe.
/// </remarks>
public static class WellKnownPluginSessionOptions
{
    /// <summary>
    /// The option key by which a plugin driver receives the host's Claude-style permission-mode selection.
    /// </summary>
    public const string PermissionMode = "permission-mode";

    /// <summary>
    /// The option key for the model — the host's driver adapter wires its typed <c>SetModelAsync</c> to a live
    /// <see cref="IPluginSessionDriver.SetLiveOptionAsync"/> under this key, so a plugin that declares
    /// <see cref="PluginSessionCapabilities.SupportsLiveModelSwitch"/> receives a mid-session model change.
    /// </summary>
    public const string Model = "model";

    /// <summary>
    /// The option key by which the host hands a plugin driver the session's own pane id (#AC-13). A provider that
    /// spawns a child process should set it as the <c>COCKPIT_PANE_ID</c> environment variable, so the agent inside
    /// can name its own session to the cockpit-session MCP server's <c>set_status</c> tool. A provider with nothing
    /// to spawn simply ignores it. The TTY route sets the variable host-side and does not use this key.
    /// </summary>
    public const string PaneId = "cockpit.pane-id";

    /// <summary>
    /// The option key by which the host hands a plugin driver a hidden system prompt to prepend for this one session
    /// (AC-180) — the "you are the CEO, this is how you plan" briefing an embedded Autopilot run gives its agent
    /// without the operator seeing it as a turn (<see cref="Workspaces.EmbeddedSessionRequest.AppendSystemPrompt"/>).
    /// </summary>
    /// <remarks>
    /// Each driver applies it its own way (Claude/Codex CLI's <c>--append-system-prompt</c>, a leading system
    /// message for an OpenAI-compatible model). A provider that cannot inject a system prompt ignores it.
    /// </remarks>
    public const string AppendSystemPrompt = "cockpit.append-system-prompt";

    /// <summary>
    /// The option key by which the host asks a driver to confine this session's file tools to its working directory
    /// (AC-174) — set to <c>"true"</c> when the host isolates an embedded session in a worktree
    /// (<see cref="Workspaces.EmbeddedSessionRequest.IsolateInWorktree"/>).
    /// </summary>
    /// <remarks>
    /// A provider that reaches files only through out-of-process MCP servers honours it by re-rooting its file
    /// servers at the working directory and dropping every server that could write or execute outside it, then
    /// reports <c>ConfinesFileAccessToWorkingDirectory = true</c>. A provider that already confines natively ignores
    /// it. The flag alone is never trusted — only a driver that actually confined sets the capability.
    /// </remarks>
    public const string ConfineFileToolsToWorkingDirectory = "cockpit.confine-file-tools";

    /// <summary>
    /// The option key by which the host tells a driver that nobody is watching this session — <c>"true"</c> for a
    /// delegated task (#67) or an embedded run that drives itself, <c>"false"</c> for a session an operator sits
    /// in front of.
    /// </summary>
    /// <remarks>
    /// The host states one or the other on every launch; a driver that finds the key missing must read that as
    /// unattended, the safe answer. A driver that narrows this session's tool surface makes that narrowing
    /// authoritative when this is set (Claude pairs <c>--mcp-config</c> with <c>--strict-mcp-config</c>) and additive
    /// when it is not. A provider with nothing to narrow ignores it.
    /// </remarks>
    public const string Unattended = "cockpit.unattended";
}
