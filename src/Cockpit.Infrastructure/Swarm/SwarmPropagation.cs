using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Swarm;
using Cockpit.Core.Toasts;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmPropagation(SwarmMembershipStore store, IToastService toasts) : ISingletonService
{
    internal static string Stamp(SwarmRecord record) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        SwarmRecordJson.Canonicalize(record.Payload)));

    internal static string? Prefix(IEnumerable<SwarmRecord> log)
    {
        var stamps = log.Select(Stamp).ToArray();
        return stamps.Length == 0 ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", stamps))));
    }

    internal async Task<SwarmRecords> RecordsAsync(long since, string? prefix, CancellationToken ct)
    {
        var membership = await store.LoadAsync(ct);
        if (since < 0 || since > membership.Sequence || Prefix(membership.Log.Take((int)since)) != prefix)
            throw new InvalidDataException("swarm_log_conflict");
        return new(since, prefix, membership.Log.Skip((int)since).ToArray());
    }

    internal async Task ApplyAsync(SwarmRecords delta, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var current = await store.LoadAsync(ct);
            if (delta.Since > current.Sequence || delta.Since < 0
                || Prefix(current.Log.Take((int)delta.Since)) != delta.Prefix)
                throw new InvalidDataException("swarm_log_conflict");
            var candidate = SwarmMembership.Replay(current.Log);
            var joined = new List<SwarmMember>();
            foreach (var record in delta.Records)
            {
                var change = record.Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options);
                if (change is not null && change.Sequence <= current.Sequence)
                {
                    if (change.Sequence < 1 || Stamp(current.Log[(int)change.Sequence - 1]) != Stamp(record))
                        throw new InvalidDataException("swarm_log_conflict");
                    continue;
                }
                if (candidate.Accept(record) != SwarmMembershipVerdict.Accepted)
                    throw new InvalidDataException("swarm_record_refused");
                if (change is SwarmMember member) joined.Add(member);
            }
            if (candidate.Sequence == current.Sequence) return;
            store.Save(candidate);
            foreach (var member in joined)
            {
                var adder = candidate.Members.FirstOrDefault(item => item.Id == member.AddedBy)?.Name ?? member.AddedBy.Value;
                toasts.Show($"{member.Name} joined the swarm, added by {adder}", ToastSeverity.Information, [], false, "Swarm", member.Id.Value);
            }
        }
        finally
        {
            store.Gate.Release();
        }
    }
}

internal sealed record SwarmRecords(long Since, string? Prefix, IReadOnlyList<SwarmRecord> Records);
