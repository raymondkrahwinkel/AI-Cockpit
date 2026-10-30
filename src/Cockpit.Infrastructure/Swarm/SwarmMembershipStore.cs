using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Swarm;

// AC-1917: keeps the membership log in `swarm-members.json` beside `cockpit.json`, replaced atomically on every save.
// Loading replays the log through `SwarmMembership.Replay`, so an altered, reordered or unsigned entry throws.
internal sealed class SwarmMembershipStore : ISingletonService
{
    private readonly string _path;
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly Lock _writeGate = new();

    public SwarmMembershipStore()
        : this(CockpitConfigPath.SwarmMembers)
    {
    }

    // Test seam: point the store at an arbitrary file.
    internal SwarmMembershipStore(string path)
    {
        _path = path;
    }

    public async Task<SwarmMembership> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new SwarmMembership();
        }

        var json = await CockpitConfigFileAccess.ReadWhenNotBeingReplacedAsync(_path, cancellationToken).ConfigureAwait(false);
        var log = JsonSerializer.Deserialize<List<SwarmRecord>>(json, SwarmRecordJson.Options)
            ?? throw new InvalidDataException($"'{_path}' holds no membership log.");
        return SwarmMembership.Replay(log);
    }

    public void Save(SwarmMembership membership)
    {
        lock (_writeGate)
        {
            if (File.Exists(_path))
            {
                var previous = JsonSerializer.Deserialize<List<SwarmRecord>>(File.ReadAllText(_path), SwarmRecordJson.Options)
                    ?? throw new InvalidDataException("Empty membership log.");
                if (previous.Count > membership.Sequence
                    || SwarmPropagation.Prefix(previous) != SwarmPropagation.Prefix(membership.Log.Take(previous.Count)))
                    throw new InvalidDataException("swarm_log_conflict");
            }
            CockpitConfigPath.ReplaceAtomicallyPrivate(_path, JsonSerializer.Serialize(membership.Log, SwarmRecordJson.Options));
        }
    }
}
