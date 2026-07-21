namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Option keys the host bridges from its own typed session-start surface into a plugin driver's
/// <see cref="IPluginSessionDriver.StartAsync(string?, string?, string?, System.Collections.Generic.IReadOnlyDictionary{string, string}?, System.Collections.Generic.IReadOnlyList{PluginMcpServer}?, System.Threading.CancellationToken)"/>
/// options map. The host's <c>ISessionDriver.StartAsync</c> carries a typed <c>permissionMode</c> (a Claude concept
/// that predates the plugin surface); the plugin contract has no such parameter, so a provider that understands
/// Claude-style permission modes declares a launch option under <see cref="PermissionMode"/> and the host's driver
/// adapter folds the operator's selection into the options map under that key. A provider that has no permission modes
/// (an HTTP model, Codex's sandbox) simply never declares the option and never reads it.
/// </summary>
public static class WellKnownPluginSessionOptions
{
    /// <summary>The option key by which a plugin driver receives the host's Claude-style permission-mode selection.</summary>
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
    /// It rides the options map like <see cref="PaneId"/>, so it reaches every provider without a signature change;
    /// each driver applies it its own way (Claude/Codex CLI's <c>--append-system-prompt</c>, a leading system message
    /// for an OpenAI-compatible model). A provider that cannot inject a system prompt ignores it — the key is safe to
    /// carry unread, the same as any other option a driver does not declare.
    /// </summary>
    public const string AppendSystemPrompt = "cockpit.append-system-prompt";
}
