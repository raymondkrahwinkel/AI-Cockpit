using System.Security.Cryptography;
using Cockpit.Core.Swarm;

namespace Cockpit.Core.Tests.Swarm;

/// <summary>
/// Layer 2, authorization/protocol (AC-1917). A membership change counts only when a current member signed it: a
/// stranger's correctly self-signed record is refused, and so is anything a removed member signs after its tombstone.
/// </summary>
public class SwarmMembershipTests
{
    [Theory]
    [InlineData("unknown-signer", SwarmMembershipVerdict.UnknownSigner)]
    [InlineData("removed-signer", SwarmMembershipVerdict.RemovedSigner)]
    public void Accept_RefusesAChangeNotSignedByACurrentMember(string forgery, SwarmMembershipVerdict expected)
    {
        using var a = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var c = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var membership = new SwarmMembership();
        Assert.Equal(SwarmMembershipVerdict.Accepted, membership.Accept(_Join(a, a, rank: 1, sequence: 1)));

        SwarmRecord forged;
        if (forgery == "unknown-signer")
        {
            // Criterion 1: B signs its own record with a valid signature, but the register does not know B.
            forged = _Join(b, b, rank: 2, sequence: 2);
            Assert.True(forged.Verify(_ => b.ExportSubjectPublicKeyInfo()));
        }
        else
        {
            // Criterion 2: A adds B, A removes B, then B adds C.
            Assert.Equal(SwarmMembershipVerdict.Accepted, membership.Accept(_Join(b, a, rank: 2, sequence: 2)));
            var tombstone = new SwarmTombstone(_Id(b), _Id(a), DateTimeOffset.UnixEpoch, Sequence: 3);
            Assert.Equal(SwarmMembershipVerdict.Accepted, membership.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(tombstone), a)));
            forged = _Join(c, b, rank: 3, sequence: 4);
        }

        Assert.Equal(expected, membership.Accept(forged));
        Assert.Equal([_Id(a)], membership.Members.Select(member => member.Id));
    }

    private static SwarmRecord _Join(ECDsa member, ECDsa adder, long rank, long sequence)
    {
        var record = new SwarmMember(member.ExportSubjectPublicKeyInfo(), $"node-{rank}", rank, ["10.0.0.1"], 7443, _Id(adder), DateTimeOffset.UnixEpoch, sequence);
        var signed = SwarmRecord.Sign(SwarmMembership.ToPayload(record), adder);
        return member == adder ? signed : signed.CoSign(member);
    }

    private static SwarmMemberId _Id(ECDsa key) => SwarmMemberId.FromPublicKey(key.ExportSubjectPublicKeyInfo());
}
