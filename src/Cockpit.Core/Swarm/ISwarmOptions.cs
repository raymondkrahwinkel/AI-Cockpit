using Cockpit.Core.Mcp;

namespace Cockpit.Core.Swarm;

public interface ISwarmOptions
{
    Task<SwarmSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(SwarmSettings settings, CancellationToken ct = default);
    Task<IReadOnlyList<SwarmMachine>> MachinesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(CancellationToken ct = default);
    Task<SwarmJoinHandshake> BeginAsync(string address, CancellationToken ct = default);
    Task<SwarmJoinHandshake> BeginManualAsync(string address, CancellationToken ct = default) => BeginAsync(address, ct);
    Task CompleteAsync(SwarmJoinHandshake handshake, bool discovered, CancellationToken ct = default);
    Task RetryAsync(CancellationToken ct = default) => Task.CompletedTask;
    SwarmJoinPending? Pending { get; }
    void Confirm(string pairingId);
    void Refuse(string pairingId);
}

public sealed record SwarmMachine(SwarmMember Member, bool IsSelf, string Origin, SwarmMemberPresence Presence);
