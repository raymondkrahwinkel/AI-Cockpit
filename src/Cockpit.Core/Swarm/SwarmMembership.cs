using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Core.Swarm;

// AC-1917: the local member register, a log of signed changes with no centre. Past the first member, who signs itself,
// a change counts only when a current member signed it; "later" is the gapless sequence every change carries, never a
// clock. A full mesh, meant for at most ten members.
public sealed class SwarmMembership
{
    private readonly List<SwarmRecord> _log = [];
    private readonly Dictionary<SwarmMemberId, SwarmMember> _members = [];
    private readonly Dictionary<SwarmMemberId, SwarmTombstone> _tombstones = [];

    // Gapless from 1, so the last accepted change's sequence is also the length of the log.
    public long Sequence => _log.Count;

    public IReadOnlyList<SwarmRecord> Log => _log;

    public IEnumerable<SwarmMember> Members => _members.Values.Where(member => !_tombstones.ContainsKey(member.Id));

    public IEnumerable<SwarmTombstone> Tombstones => _tombstones.Values;

    // The resolver for `SwarmRecord.Verify`: current members only, so a removed member's signature no longer verifies.
    public byte[]? PublicKeyOf(SwarmMemberId id) => _tombstones.ContainsKey(id) ? null : _members.GetValueOrDefault(id)?.PublicKey;

    public static JsonElement ToPayload(SwarmChange change) => JsonSerializer.SerializeToElement(change, SwarmRecordJson.Options);

    // Rebuilds a register from its log through `Accept`; the first entry `Accept` refuses throws. A log cut short at its
    // end is still a valid log and loads as the shorter register.
    public static SwarmMembership Replay(IEnumerable<SwarmRecord> log)
    {
        var membership = new SwarmMembership();
        foreach (var record in log)
        {
            var verdict = membership.Accept(record);
            if (verdict != SwarmMembershipVerdict.Accepted)
            {
                throw new InvalidDataException($"Swarm membership change {membership.Sequence + 1} is refused: {verdict}.");
            }
        }

        return membership;
    }

    public SwarmMembershipVerdict Accept(SwarmRecord record)
    {
        var verdict = _Judge(record, out var change);
        if (verdict != SwarmMembershipVerdict.Accepted)
        {
            return verdict;
        }

        switch (change)
        {
            case SwarmMember member:
                _members[member.Id] = member;
                _tombstones.Remove(member.Id);
                break;
            case SwarmTombstone tombstone:
                _tombstones.Add(tombstone.Member, tombstone);
                break;
            case SwarmRankChange rank:
                _members[rank.Member] = _members[rank.Member] with { Rank = rank.Rank };
                break;
        }

        _log.Add(record);
        return verdict;
    }

    private SwarmMembershipVerdict _Judge(SwarmRecord record, out SwarmChange? change)
    {
        change = _TryRead(record.Payload);
        if (change is null)
        {
            return SwarmMembershipVerdict.Malformed;
        }

        if (change.Sequence != Sequence + 1)
        {
            return SwarmMembershipVerdict.OutOfSequence;
        }

        var signers = record.Signatures.Select(signature => signature.MemberId).ToHashSet();
        var verdict = change switch
        {
            SwarmMember member => _JudgeMember(member, signers),
            SwarmTombstone tombstone => _JudgeTombstone(tombstone, signers),
            SwarmRankChange rank => PublicKeyOf(rank.Member) is null ? SwarmMembershipVerdict.UnknownMember
                : !signers.Contains(rank.Member) ? SwarmMembershipVerdict.MissingSignature
                : Members.Any(member => member.Rank >= rank.Rank) ? SwarmMembershipVerdict.RankTaken
                : SwarmMembershipVerdict.Accepted,
            _ => SwarmMembershipVerdict.Malformed,
        };
        if (verdict != SwarmMembershipVerdict.Accepted)
        {
            return verdict;
        }

        // A new member signs with the key in its own record; every other signer must be a member that is not removed.
        // Every tombstone held is older than this change, since sequences are gapless.
        var newMember = change as SwarmMember;
        foreach (var signer in signers)
        {
            if (signer == newMember?.Id)
            {
                continue;
            }

            if (!_members.ContainsKey(signer))
            {
                return SwarmMembershipVerdict.UnknownSigner;
            }

            if (_tombstones.ContainsKey(signer))
            {
                return SwarmMembershipVerdict.RemovedSigner;
            }
        }

        var signed = record.Verify(id => newMember is { } joining && id == joining.Id ? joining.PublicKey : PublicKeyOf(id));
        return signed ? SwarmMembershipVerdict.Accepted : SwarmMembershipVerdict.BadSignature;
    }

    private SwarmMembershipVerdict _JudgeMember(SwarmMember member, HashSet<SwarmMemberId> signers)
    {
        if (string.IsNullOrWhiteSpace(member.Name) || member.Port is <= IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            return SwarmMembershipVerdict.Malformed;
        }

        if (_tombstones.ContainsKey(member.Id) && member.Rank == _members[member.Id].Rank)
        {
            return SwarmMembershipVerdict.Removed;
        }

        if (_members.ContainsKey(member.Id) && !_tombstones.ContainsKey(member.Id))
        {
            return SwarmMembershipVerdict.AlreadyMember;
        }

        // Only the first member adds itself; after that the adder must already be a member.
        if (member.AddedBy == member.Id ? Sequence > 0 : !_members.ContainsKey(member.AddedBy))
        {
            return SwarmMembershipVerdict.UnknownSigner;
        }

        if (!signers.Contains(member.AddedBy) || !signers.Contains(member.Id))
        {
            return SwarmMembershipVerdict.MissingSignature;
        }

        return Members.Any(existing => existing.Rank == member.Rank) ? SwarmMembershipVerdict.RankTaken : SwarmMembershipVerdict.Accepted;
    }

    private SwarmMembershipVerdict _JudgeTombstone(SwarmTombstone tombstone, HashSet<SwarmMemberId> signers)
    {
        if (_tombstones.ContainsKey(tombstone.Member))
        {
            return SwarmMembershipVerdict.Removed;
        }

        if (!_members.ContainsKey(tombstone.Member))
        {
            return SwarmMembershipVerdict.UnknownMember;
        }

        return signers.Contains(tombstone.RemovedBy) ? SwarmMembershipVerdict.Accepted : SwarmMembershipVerdict.MissingSignature;
    }

    private static SwarmChange? _TryRead(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<SwarmChange>(SwarmRecordJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

// One change to the register, tagged by `kind` in the signed payload so a signature on one kind cannot be read as another.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SwarmMember), "member")]
[JsonDerivedType(typeof(SwarmTombstone), "tombstone")]
[JsonDerivedType(typeof(SwarmRankChange), "rank")]
public abstract record SwarmChange(long Sequence);

public sealed record SwarmRankChange(SwarmMemberId Member, long Rank, long Sequence) : SwarmChange(Sequence);

// The id is the hash of `PublicKey`, so a record cannot name a member whose key it does not carry.
public sealed record SwarmMember(
    byte[] PublicKey,
    string Name,
    long Rank,
    IReadOnlyList<string> Addresses,
    int Port,
    SwarmMemberId AddedBy,
    DateTimeOffset AddedAt,
    long Sequence) : SwarmChange(Sequence)
{
    [JsonIgnore]
    public SwarmMemberId Id => SwarmMemberId.FromPublicKey(PublicKey);
}

public sealed record SwarmTombstone(SwarmMemberId Member, SwarmMemberId RemovedBy, DateTimeOffset RemovedAt, long Sequence)
    : SwarmChange(Sequence);

public enum SwarmMembershipVerdict
{
    Accepted,
    Malformed,
    OutOfSequence,
    UnknownSigner,
    RemovedSigner,
    MissingSignature,
    BadSignature,
    AlreadyMember,
    Removed,
    UnknownMember,
    RankTaken,
}
