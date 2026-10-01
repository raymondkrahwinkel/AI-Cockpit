namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// AC-1330: appends the behaviour rules this cockpit and a paired node lack from each other, both ways.
/// </summary>
public interface IBehaviourMemorySync
{
    /// <summary>
    /// Runs one exchange with <paramref name="nodeName"/>; a failed read appends nothing on either side.
    /// </summary>
    Task RunAsync(string nodeName, CancellationToken cancellationToken = default);
}
