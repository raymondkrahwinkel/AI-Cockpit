using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2279: the ordinary Swarm page of a remote window, reading and changing the server's swarm. This window never
// joins it: no identity, no pairing, no membership here; one status read per refresh answers every question of it.
internal sealed class RemoteSwarmOptions(Func<BackendApiClient> client) : ISwarmOptions, ISwarmLeadership
{
    private const string Route = "api/v1/options/swarm";
    private const string PairingRefusal = "Pair machines on the server's own screen; a remote window does not pair.";

    private const string TakeoverRefusal = "Answer a takeover on the server's own screen.";

    private SwarmStatus? _status;

    public SwarmJoinPending? Pending => null;
    public string ConnectionFailure => _status?.ConnectionFailure ?? "";
    public SwarmMember? Current => _status?.Leader;
    public bool IsSelf => _status?.LeaderIsSelf ?? false;
    public SwarmTakeover? PendingTakeover => _status?.PendingTakeover;

    // The page refreshes on its own timer, so there is nothing to announce.
    public event EventHandler? Changed { add { } remove { } }

    private async Task<SwarmStatus> _ReadAsync(CancellationToken ct) =>
        _status = await client().SendAsync<SwarmStatus>(HttpMethod.Get, Route, null, ConnectKeyEndpoints.Json, ct).ConfigureAwait(false);

    private async Task _PostAsync(string action, CancellationToken ct) =>
        await client().SendAsync<System.Text.Json.JsonElement>(HttpMethod.Post, $"{Route}/{action}", null, ConnectKeyEndpoints.Json, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SwarmMachine>> MachinesAsync(CancellationToken ct = default) => (await _ReadAsync(ct).ConfigureAwait(false)).Machines;

    public async Task<SwarmSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        var status = _status ?? await _ReadAsync(ct).ConfigureAwait(false);
        return new()
        {
            ExtraInternalCidrs = status.ExtraInternalCidrs,
            WhenALowerRankComesBack = status.JoinAtBack ? SwarmLowerRankReturn.JoinAtBack : SwarmLowerRankReturn.AnnouncedTakeover,
            ManualAddresses = status.ManualAddresses,
        };
    }

    public async Task SaveSettingsAsync(SwarmSettings settings, CancellationToken ct = default) =>
        _status = await client().SendAsync<SwarmStatus>(HttpMethod.Put, Route,
            new SwarmOptionsBody(settings.ExtraInternalCidrs, settings.WhenALowerRankComesBack == SwarmLowerRankReturn.JoinAtBack),
            ConnectKeyEndpoints.Json, ct).ConfigureAwait(false);

    public Task<bool> ConnectionsEnabledAsync(CancellationToken ct = default) => Task.FromResult(_status?.ConnectionsEnabled ?? true);
    public Task<string> RemovalStatusAsync(CancellationToken ct = default) => Task.FromResult(_status?.RemovalStatus ?? "");
    public Task KickAsync(SwarmMemberId member, CancellationToken ct = default) => _PostAsync($"members/{member.Value}/remove", ct);
    public Task RetryAsync(CancellationToken ct = default) => _PostAsync("retry", ct);
    public Task ClaimLeadershipAsync(CancellationToken ct = default) => _PostAsync("leadership", ct);

    public async Task<string> ResetAsync(CancellationToken ct = default) =>
        (await client().SendAsync<System.Text.Json.JsonElement>(HttpMethod.Post, $"{Route}/reset", null, ConnectKeyEndpoints.Json, ct).ConfigureAwait(false))
            .GetProperty("backup").GetString() ?? "";

    public Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(CancellationToken ct = default) => throw new NotSupportedException(PairingRefusal);
    public Task<SwarmJoinHandshake> BeginAsync(string address, CancellationToken ct = default) => throw new NotSupportedException(PairingRefusal);
    public Task CompleteAsync(SwarmJoinHandshake handshake, bool discovered, CancellationToken ct = default) => throw new NotSupportedException(PairingRefusal);
    public void Confirm(string pairingId) => throw new NotSupportedException(PairingRefusal);
    public void Refuse(string pairingId) => throw new NotSupportedException(PairingRefusal);
    public Task TakeOverNowAsync(CancellationToken ct = default) => throw new NotSupportedException(TakeoverRefusal);
    public Task StayWithCurrentAsync(CancellationToken ct = default) => throw new NotSupportedException(TakeoverRefusal);
}
