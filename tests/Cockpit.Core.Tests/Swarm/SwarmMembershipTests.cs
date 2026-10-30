using System.Security.Cryptography;
using Cockpit.Core.Swarm;

namespace Cockpit.Core.Tests.Swarm;

/// <summary>
/// Layer 2, authorization/protocol (AC-1917). A membership change counts only when a current member signed it: a
/// stranger's correctly self-signed record is refused, and so is anything a removed member signs after its tombstone.
/// </summary>
public class SwarmMembershipTests
{
    // Per row, built from keys A, B and C: the changes the register accepts first, and last the forgery it must refuse.
    public static TheoryData<Func<ECDsa[], SwarmRecord[]>, SwarmMembershipVerdict> Forgeries => new()
    {
        // Criterion 1: B signs its own record with a valid signature, but the register does not know B.
        { k => [_Join(k[0], k[0], rank: 1, sequence: 1), _Join(k[1], k[1], rank: 2, sequence: 2)], SwarmMembershipVerdict.UnknownSigner },

        // Criterion 2: A adds B, A removes B, then B adds C.
        {
            k => [_Join(k[0], k[0], rank: 1, sequence: 1), _Join(k[1], k[0], rank: 2, sequence: 2), _Remove(k[1], k[0], sequence: 3), _Join(k[2], k[1], rank: 3, sequence: 4)],
            SwarmMembershipVerdict.RemovedSigner
        },
    };

    [Theory]
    [MemberData(nameof(Forgeries))]
    public void Accept_RefusesAChangeNotSignedByACurrentMember(Func<ECDsa[], SwarmRecord[]> build, SwarmMembershipVerdict expected)
    {
        using var a = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var c = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECDsa[] keys = [a, b, c];
        var changes = build(keys);
        var forged = changes[^1];
        var membership = new SwarmMembership();

        Assert.All(changes[..^1], change => Assert.Equal(SwarmMembershipVerdict.Accepted, membership.Accept(change)));
        Assert.True(forged.Verify(id => keys.Select(key => key.ExportSubjectPublicKeyInfo()).FirstOrDefault(spki => SwarmMemberId.FromPublicKey(spki) == id)));
        Assert.Equal(expected, membership.Accept(forged));
        Assert.Equal([_Id(a)], membership.Members.Select(member => member.Id));
    }

    private static SwarmRecord _Join(ECDsa member, ECDsa adder, long rank, long sequence)
    {
        var record = new SwarmMember(member.ExportSubjectPublicKeyInfo(), $"node-{rank}", rank, ["10.0.0.1"], 7443, _Id(adder), DateTimeOffset.UnixEpoch, sequence);
        var signed = SwarmRecord.Sign(SwarmMembership.ToPayload(record), adder);
        return member == adder ? signed : signed.CoSign(member);
    }

    private static SwarmRecord _Remove(ECDsa member, ECDsa remover, long sequence) =>
        SwarmRecord.Sign(SwarmMembership.ToPayload(new SwarmTombstone(_Id(member), _Id(remover), DateTimeOffset.UnixEpoch, sequence)), remover);

    private static SwarmMemberId _Id(ECDsa key) => SwarmMemberId.FromPublicKey(key.ExportSubjectPublicKeyInfo());
}
