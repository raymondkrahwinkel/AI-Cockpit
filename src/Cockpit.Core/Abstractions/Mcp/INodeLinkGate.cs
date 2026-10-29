namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// AC-1943: whether this controller may talk to a paired node at all. A node the operator disconnected (this run) or
/// paused (kept across restarts, the pairing untouched) is held: nothing is polled, relayed or started there, and no
/// mail about it reaches the assistant. One gate for every caller, so a path nobody remembered cannot leak through.
/// </summary>
public interface INodeLinkGate
{
    bool IsPaused(string nodeName);

    /// <summary>
    /// Null when calls to <paramref name="nodeName"/> may go out, else the sentence that says why they may not.
    /// </summary>
    string? RefusalFor(string nodeName);

    void SetPaused(string nodeName, bool paused);

    void SetDisconnected(string nodeName, bool disconnected);
}
