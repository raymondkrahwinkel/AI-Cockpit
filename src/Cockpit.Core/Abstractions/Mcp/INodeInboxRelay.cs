namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// AC-1322: carries a paired node's mail for the assistant, and its reachability changes, into the local inbox.
/// </summary>
public interface INodeInboxRelay
{
    /// <summary>
    /// Collects what agents on <paramref name="nodeName"/> sent the assistant since the last delivered message.
    /// </summary>
    Task PollAsync(string nodeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the assistant <paramref name="nodeName"/> became reachable or stopped answering.
    /// </summary>
    void NotifyTransition(string nodeName, bool reachable, int sessionCount, DateTimeOffset atUtc);
}
