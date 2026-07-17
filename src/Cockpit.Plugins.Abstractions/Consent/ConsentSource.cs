namespace Cockpit.Plugins.Abstractions.Consent;

/// <summary>
/// Who is asking — carried into the audit trail so "what did this agent get me to approve" is answerable
/// afterwards, and used to pin the prompt to the session it belongs to.
/// </summary>
/// <param name="PaneId">
/// The session pane the request belongs to (<c>IPluginSessionContext.PaneId</c>), so the prompt can appear on
/// that session and a remembered approval is scoped to it. Null for a request that belongs to no single pane.
/// </param>
/// <param name="PluginId">The plugin that asked, when the request came through <see cref="ICockpitHost"/>. Null for a host-internal caller. Set by the host, not the caller.</param>
/// <param name="Label">A short human name for the source, shown on the prompt and logged — "Workflows", "Terminal MCP".</param>
public sealed record ConsentSource(string? PaneId, string? PluginId, string Label);
