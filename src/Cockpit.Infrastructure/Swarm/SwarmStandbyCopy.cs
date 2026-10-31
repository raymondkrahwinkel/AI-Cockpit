using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Sessions;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Swarm;

internal sealed record SwarmStandbySnapshot(string LeaderId, Guid Generation, long Revision, DateTimeOffset At,
    TranscriptSnapshotEntry[] Transcript, string NoteState, string Behaviour, SwarmAssistantReceipt[] Receipts, AgentMessage[] PendingInbox,
    DateTimeOffset NoteStateAt = default);

internal sealed record SwarmAssistantReceipt(string Source, Guid MessageId, string Digest);

internal sealed class SwarmStandbyCopy : ISingletonService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly Lock _gate = new();

    public SwarmStandbyCopy() : this(Path.Combine(CockpitConfigPath.Root, "swarm-standby.json")) { }
    internal SwarmStandbyCopy(string path) => _path = path;
    internal string Folder => Path.GetDirectoryName(_path) ?? CockpitConfigPath.Root;

    internal SwarmStandbySnapshot? Read()
    {
        lock (_gate)
            return File.Exists(_path) ? JsonSerializer.Deserialize<SwarmStandbySnapshot>(File.ReadAllText(_path), Json)
                ?? throw new InvalidDataException("Empty standby copy.") : null;
    }

    internal void Save(SwarmStandbySnapshot snapshot)
    {
        lock (_gate)
        {
            var previous = Read();
            if (previous?.Generation == snapshot.Generation && previous.Revision >= snapshot.Revision) return;
            if (previous?.LeaderId == snapshot.LeaderId && previous.At > snapshot.At) return;
            CockpitConfigPath.ReplaceAtomicallyPrivate(_path, stream => JsonSerializer.Serialize(stream, snapshot, Json));
        }
    }

    internal async Task ReceiveAsync(Stream stream, SwarmMemberId source, CancellationToken ct, ISwarmLeadership? leadership = null)
    {
        var snapshot = Validated(await JsonSerializer.DeserializeAsync<SwarmStandbySnapshot>(stream, Json, ct), source);
        if (leadership is null) Save(snapshot);
        else if (!leadership.WithLeader(leader =>
        {
            if (leader?.Id != source) return false;
            Save(snapshot);
            return true;
        })) throw new InvalidOperationException("The standby sender no longer leads.");
    }

    internal static SwarmStandbySnapshot Validated(SwarmStandbySnapshot? snapshot, SwarmMemberId source)
    {
        if (snapshot is null)
        {
            throw new InvalidDataException("Empty standby copy.");
        }
        if (snapshot.LeaderId != source.Value || snapshot.Generation == Guid.Empty || snapshot.Revision < 1
            || snapshot.Transcript is null || snapshot.NoteState is null || snapshot.Behaviour is null || snapshot.Receipts is null || snapshot.PendingInbox is null
            || snapshot.Transcript.Any(row => row is null || string.IsNullOrWhiteSpace(row.Id))
            || snapshot.Transcript.Select(row => row.Id).Distinct().Count() != snapshot.Transcript.Length
            || snapshot.Receipts.Any(receipt => receipt is null || !SwarmMemberId.TryParse(receipt.Source, out _)
                || receipt.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(receipt.Digest)))
            throw new InvalidDataException("Invalid standby copy.");
        return snapshot;
    }
}
