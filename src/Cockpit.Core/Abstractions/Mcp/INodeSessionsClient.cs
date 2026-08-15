using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// The controller's side of AC-795: what runs on a paired node, and starting or stopping one of those sessions
/// from here. The mirror of <c>NodeSessionMcpTools</c>, which is what this talks to — one call per tool, over the
/// same pinned, shared-secret connection the pairing left in the MCP registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reaches the same four tools the assistant does, and no more.</b> The assistant on this cockpit gets at a
/// node through the registry row the pairing wrote, so its whole reach is that node's <c>cockpit-node</c> server.
/// This interface exists so the operator's own screen has that same reach and not a wider one — criterion 5 of
/// AC-795 is a statement about both directions, and the cheapest way to hold it is for the UI to have no private
/// route of its own.
/// </para>
/// <para>
/// <b>Nothing here is cached.</b> A node is another machine: what it was running a minute ago is not what it is
/// running now, and a stale list is how a stop lands on the wrong row. Every read is a call.
/// </para>
/// </remarks>
public interface INodeSessionsClient
{
    /// <summary>The nodes this cockpit is paired with, by name, as the MCP registry records them.</summary>
    Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// What is running on <paramref name="nodeName"/> and what may be started there. Never throws for an
    /// unreachable node — a node that is off, asleep or off the network is an ordinary state of this feature, and
    /// it comes back as <see cref="NodeSessionsSnapshot.Error"/> rather than an exception.
    /// </summary>
    Task<NodeSessionsSnapshot> ReadAsync(string nodeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a session on <paramref name="nodeName"/> under <paramref name="profileLabel"/>, optionally for one of
    /// that node's allowed projects. Returns null when it started, or the node's own refusal — which is where the
    /// scope grant (AC-794) is answered, so a refusal here is normal and worth showing verbatim.
    /// </summary>
    Task<string?> StartAsync(
        string nodeName,
        string profileLabel,
        string? projectId = null,
        string? prompt = null,
        string? sessionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the session with <paramref name="paneId"/> on <paramref name="nodeName"/>. Returns null when it
    /// stopped, or the reason it did not. The pane id belongs to that node and means nothing on this machine.
    /// </summary>
    Task<string?> StopAsync(string nodeName, string paneId, CancellationToken cancellationToken = default);
}

// One node, as the controller last read it. A non-null `Error` means nothing else here is current.
public sealed record NodeSessionsSnapshot(
    string NodeName,
    IReadOnlyList<NodeSessionRow> Sessions,
    IReadOnlyList<NodeScopedProfileSummary> Profiles,
    IReadOnlyList<NodeProjectRow> Projects,
    string? Error = null);

// One session running on a node. The pane id is that machine's, never this one's.
public sealed record NodeSessionRow(string PaneId, string Name, string Profile, string Statusline);

// One project a node's operator has allowed this controller to start work on.
public sealed record NodeProjectRow(string Id, string Name);
