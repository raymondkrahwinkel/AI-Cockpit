using Cockpit.Core.Abstractions.Assistant;
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
    /// that node's allowed projects. <see cref="NodeStartResult.Error"/> is null when it started, else the node's own
    /// refusal — which is where the scope grant (AC-794) is answered, so a refusal here is normal and worth showing verbatim.
    /// </summary>
    Task<NodeStartResult> StartAsync(
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

    /// <summary>
    /// AC-1323: hands the session with <paramref name="paneId"/> on <paramref name="nodeName"/> a turn — what
    /// <c>send_prompt</c> does locally. Null when it went in, else the node's refusal or the reason it was not reached.
    /// </summary>
    Task<string?> SendPromptAsync(string nodeName, string paneId, string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// AC-1323: leaves a message in the inbox of the session with <paramref name="paneId"/> on <paramref name="nodeName"/>,
    /// sent as that node's assistant. Null when delivered, else the refusal or the reason the node was not reached.
    /// </summary>
    Task<string?> SendMessageAsync(string nodeName, string paneId, string kind, string body, CancellationToken cancellationToken = default);

    /// <summary>
    /// AC-1323: renames the session with <paramref name="paneId"/> on <paramref name="nodeName"/>. Null when renamed,
    /// else the refusal or the reason the node was not reached.
    /// </summary>
    Task<string?> RenameAsync(string nodeName, string paneId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// AC-1323: the last <paramref name="count"/> transcript rows of the session with <paramref name="paneId"/> on
    /// <paramref name="nodeName"/>, in the same shape the local read gateway returns them. Never throws for an
    /// unreachable node — see <see cref="NodeTranscriptRead.Error"/>.
    /// </summary>
    Task<NodeTranscriptRead> ReadTranscriptAsync(string nodeName, string paneId, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// AC-1322: the messages agents on <paramref name="nodeName"/> addressed to their assistant while this cockpit
    /// was its controller, oldest first. <paramref name="afterMessageId"/> is the last id this cockpit already
    /// holds: the node drops everything up to and including it and returns what follows, so a poll that fails
    /// leaves the node holding the same messages for the next one. Never throws for an unreachable node — see <see cref="NodeInboxBatch.Error"/>.
    /// </summary>
    Task<NodeInboxBatch> ReadInboxAsync(string nodeName, string? afterMessageId, CancellationToken cancellationToken = default);
}

// AC-1323: what a start on a node came back with — the node's pane id (that machine's, never this one's) and the
// profile that actually ran, or `Error`, in which case nothing else here is set.
public sealed record NodeStartResult(
    string? Error,
    string PaneId = "",
    string SessionName = "",
    string ResolvedProfile = "",
    bool? PromptDelivered = null);

// AC-1323: one transcript read on a node. A non-null `Error` means nothing was read; `Transcript` null with no
// error means the node knows no such session.
public sealed record NodeTranscriptRead(AssistantTranscript? Transcript, string? Error = null);

// AC-1322: one read of a node's queue for its controller. A non-null `Error` means nothing was read and the
// caller's cursor must not move. `Remaining` is what the node still holds beyond this batch.
public sealed record NodeInboxBatch(
    string NodeName,
    IReadOnlyList<NodeInboxMessage> Messages,
    int Remaining = 0,
    string? Error = null,
    string DiscoveryId = "");

// One message an agent on a node sent to `cockpit-assistant` there. `FromPaneId` is that machine's pane id.
public sealed record NodeInboxMessage(string Id, string FromPaneId, string Kind, string Body, DateTimeOffset SentAtUtc);

// One node, as the controller last read it. A non-null `Error` means nothing else here is current.
// AC-796, criterion 2: `Error` carries the distinction between "connection down" and "node looks stopped"
// where classifiable (see `NodeSessionsClient.Classify`); no separate typed field for it.

// AC-1320: `DiscoveryId` is the node's own stable machine id as it reports it — empty from a node build that
// predates the field, in which case the name is all the origin there is.
public sealed record NodeSessionsSnapshot(
    string NodeName,
    IReadOnlyList<NodeSessionRow> Sessions,
    IReadOnlyList<NodeScopedProfileSummary> Profiles,
    IReadOnlyList<NodeProjectRow> Projects,
    string? Error = null,
    string DiscoveryId = "");

// One session running on a node. The pane id is that machine's, never this one's. Status, NeedsYou and
// HasOutstandingWork mean what they mean on the assistant's own list_sessions (AC-1320).
public sealed record NodeSessionRow(
    string PaneId,
    string Name,
    string Profile,
    string Statusline,
    string Status = "",
    bool NeedsYou = false,
    bool HasOutstandingWork = false);

// One project a node's operator has allowed this controller to start work on.
public sealed record NodeProjectRow(string Id, string Name);
