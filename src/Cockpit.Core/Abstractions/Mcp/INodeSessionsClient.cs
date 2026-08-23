using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// The controller's side of AC-795: what runs on a paired node, starting or stopping sessions there. Mirrors
/// <c>NodeSessionMcpTools</c> over the same pinned, shared-secret connection the pairing left in the MCP registry —
/// the same four tools the assistant reaches, giving the operator's screen the same reach and no wider (criterion 5). Nothing here is cached — a node is another machine, and a stale list is how a stop lands on the wrong row.
/// </summary>
public interface INodeSessionsClient
{
    /// <summary>
    /// The nodes this cockpit is paired with, by name, as the MCP registry records them.
    /// </summary>
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
//
// AC-796, criterion 2: `Error` itself carries the distinction between "the connection is down" and "the node
// looks stopped" where that can be told apart — see `NodeSessionsClient.Classify` for the exception shapes each
// wording comes from — and the same honest "could not reach" as before for a failure that cannot be classified.
// No separate typed field for it: nothing on this cockpit reads a node's failure kind as anything but text for the
// operator, so a taxonomy alongside the sentence would be a second thing to keep in sync with the first.
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
