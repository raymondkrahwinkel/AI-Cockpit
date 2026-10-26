namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// What kind of caller is behind an MCP tool call, as the host verified it from the transport (AC-1685).
/// </summary>
public enum McpCallerKind
{
    /// <summary>The owner's own assistant session.</summary>
    Assistant,

    /// <summary>An ordinary session on this cockpit, role sessions included.</summary>
    Session,

    /// <summary>A caller that came in over the node listener: a connect key, or the paired controller.</summary>
    Remote,
}

/// <summary>
/// The transport-verified caller of the MCP tool call being handled (AC-1685). The host stamps every field from
/// the request's own credential and never from a tool argument, so a session cannot claim to be the assistant.
/// </summary>
/// <param name="PaneId">The caller's pane id, the same value <see cref="ICockpitHost.CurrentMcpCallerPaneId"/> reports.</param>
/// <param name="Kind">Whether the caller is the assistant, an ordinary session or a remote caller.</param>
/// <param name="ViaConnectKeyLabel">The connect key's label for a <see cref="McpCallerKind.Remote"/> caller that used one; null otherwise, the pairing included.</param>
public sealed record McpCaller(string PaneId, McpCallerKind Kind, string? ViaConnectKeyLabel = null);
