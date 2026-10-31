namespace Cockpit.Core.Swarm;

/// <summary>Routes assistant input to the elected swarm leader.</summary>
public interface ISwarmAssistantRelay
{
    /// <summary>Whether this machine belongs to the swarm.</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
    /// <summary>Starts the leader's assistant before opening its conversation.</summary>
    Task EnsureStartedAsync(CancellationToken ct = default);
    /// <summary>Waits until the leader has accepted this input.</summary>
    Task SendAsync(string text, IReadOnlyList<byte[]> images, CancellationToken ct = default);
}
