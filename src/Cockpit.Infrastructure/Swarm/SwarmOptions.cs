using Cockpit.Core.Abstractions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmOptions(SwarmSettingsStore settings, SwarmMembershipStore membership,
    SwarmIdentity identity, SwarmReachability reachability, SwarmJoinClient join, SwarmJoinHost host,
    SwarmManualAddress manual, SwarmHeartbeat heartbeat, SwarmKick kick)
    : ISwarmOptions, ISingletonService
{
    public event Action<SwarmMember>? RemovalRequested
    {
        add => kick.RemovalRequested += value;
        remove => kick.RemovalRequested -= value;
    }
    public SwarmJoinPending? Pending => host.Pending;
    public Task<string> RemovalStatusAsync(CancellationToken ct = default) => kick.StatusAsync(ct);
    public Task KickAsync(SwarmMemberId member, CancellationToken ct = default) => kick.RemoveAsync(member, ct);
    public Task<SwarmSettings> LoadSettingsAsync(CancellationToken ct = default) => settings.LoadAsync(ct);

    public async Task SaveSettingsAsync(SwarmSettings value, CancellationToken ct = default)
    {
        await settings.UpdateAsync(current => value with { PairingOrigins = current.PairingOrigins, ManualAddresses = current.ManualAddresses }, ct);
    }

    public async Task<IReadOnlyList<SwarmMachine>> MachinesAsync(CancellationToken ct = default)
    {
        var register = await membership.LoadAsync(ct);
        if (register.Sequence > 0 && register.PublicKeyOf(identity.MemberId) is null) return [];
        var options = await settings.LoadAsync(ct);
        return register.Members.OrderBy(member => member.Rank).Select(member => new SwarmMachine(member,
            member.Id == identity.MemberId,
            options.PairingOrigins.GetValueOrDefault(member.Id.Value)
                ?? $"Added by {register.Members.FirstOrDefault(item => item.Id == member.AddedBy)?.Name ?? member.AddedBy.Value}",
            member.Id == identity.MemberId ? new(SwarmPresence.Online, DateTimeOffset.UtcNow, true) : reachability.Get(member.Id))).ToArray();
    }

    public Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(CancellationToken ct = default) => join.FindAsync(TimeSpan.FromSeconds(3), ct);
    public Task<SwarmJoinHandshake> BeginAsync(string address, CancellationToken ct = default) => join.BeginAsync(address, ct);

    public async Task<SwarmJoinHandshake> BeginManualAsync(string address, CancellationToken ct = default)
    {
        var uri = SwarmManualAddress.Parse(address);
        await manual.RememberAsync(uri.ToString(), ct: ct);
        try { return await join.BeginAsync(uri.ToString(), ct); }
        catch (Exception ex)
        {
            await manual.RememberAsync(uri.ToString(), failure: ex, ct: ct);
            throw;
        }
    }

    public Task RetryAsync(CancellationToken ct = default) => heartbeat.TickAsync(ct: ct);

    public async Task CompleteAsync(SwarmJoinHandshake handshake, bool discovered, CancellationToken ct = default)
    {
        try { await join.CompleteAsync(handshake, ct); }
        catch (Exception ex)
        {
            if (!discovered) await manual.RememberAsync(handshake.Address, failure: ex, ct: ct);
            throw;
        }
        await settings.UpdateAsync(current => current with { PairingOrigins = new Dictionary<string, string>(current.PairingOrigins)
        {
            [SwarmMemberId.FromPublicKey(handshake.PublicKey).Value] = discovered ? "Discovered" : "Manual address",
        } }, ct);
    }

    public void Confirm(string pairingId) => host.Confirm(pairingId);
    public void Refuse(string pairingId) => host.Refuse(pairingId);
}
