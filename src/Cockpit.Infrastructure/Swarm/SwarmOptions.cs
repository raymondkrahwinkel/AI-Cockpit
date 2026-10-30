using Cockpit.Core.Abstractions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmOptions(SwarmSettingsStore settings, SwarmMembershipStore membership,
    SwarmIdentity identity, SwarmReachability reachability, SwarmJoinClient join, SwarmJoinHost host)
    : ISwarmOptions, ISingletonService
{
    public SwarmJoinPending? Pending => host.Pending;
    public Task<SwarmSettings> LoadSettingsAsync(CancellationToken ct = default) => settings.LoadAsync(ct);

    public async Task SaveSettingsAsync(SwarmSettings value, CancellationToken ct = default)
    {
        var current = await settings.LoadAsync(ct);
        await settings.SaveAsync(value with { PairingOrigins = current.PairingOrigins }, ct);
    }

    public async Task<IReadOnlyList<SwarmMachine>> MachinesAsync(CancellationToken ct = default)
    {
        var register = await membership.LoadAsync(ct);
        var options = await settings.LoadAsync(ct);
        return register.Members.OrderBy(member => member.Rank).Select(member => new SwarmMachine(member,
            member.Id == identity.MemberId,
            options.PairingOrigins.GetValueOrDefault(member.Id.Value)
                ?? $"Added by {register.Members.FirstOrDefault(item => item.Id == member.AddedBy)?.Name ?? member.AddedBy.Value}",
            member.Id == identity.MemberId ? new(SwarmPresence.Online, DateTimeOffset.UtcNow, true) : reachability.Get(member.Id))).ToArray();
    }

    public Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(CancellationToken ct = default) => join.FindAsync(TimeSpan.FromSeconds(3), ct);
    public Task<SwarmJoinHandshake> BeginAsync(string address, CancellationToken ct = default) => join.BeginAsync(address, ct);

    public async Task CompleteAsync(SwarmJoinHandshake handshake, bool discovered, CancellationToken ct = default)
    {
        await join.CompleteAsync(handshake, ct);
        var current = await settings.LoadAsync(ct);
        var origins = new Dictionary<string, string>(current.PairingOrigins)
        {
            [SwarmMemberId.FromPublicKey(handshake.PublicKey).Value] = discovered ? "Discovered" : "Manual address",
        };
        await settings.SaveAsync(current with { PairingOrigins = origins }, ct);
    }

    public void Confirm(string pairingId) => host.Confirm(pairingId);
    public void Refuse(string pairingId) => host.Refuse(pairingId);
}
