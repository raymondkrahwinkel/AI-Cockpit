using System.Security.Cryptography;
using Cockpit.Core.Swarm;

namespace Cockpit.Core.Tests.Swarm;

public class SwarmRankCollisionTests
{
    [Fact]
    public void MostRecentlyAddedMemberDrawsANewRankSoOneLeaderRemains()
    {
        using var desktopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var phoneKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var tabletKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var desktop = new SwarmMember(desktopKey.ExportSubjectPublicKeyInfo(), "Desktop", 17, ["10.0.0.1"], 7443,
            SwarmMemberId.FromPublicKey(desktopKey.ExportSubjectPublicKeyInfo()), DateTimeOffset.UnixEpoch, 1);
        SwarmMember Joined(ECDsa key, string name, int minute) => new(key.ExportSubjectPublicKeyInfo(), name, 5, ["10.0.0.2"], 7443,
            desktop.Id, DateTimeOffset.UnixEpoch.AddMinutes(minute), 2);
        var phone = Joined(phoneKey, "Phone", 1);
        var tablet = Joined(tabletKey, "Tablet", 2);
        SwarmMembership Side(SwarmMember member, ECDsa key)
        {
            var register = new SwarmMembership();
            Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(desktop), desktopKey)));
            Assert.Equal(SwarmMembershipVerdict.Accepted,
                register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(member), desktopKey).CoSign(key)));
            return register;
        }
        var phoneSide = Side(phone, phoneKey);
        var tabletSide = Side(tablet, tabletKey);

        Assert.Null(SwarmLeaderElection.ResolveRankCollision(phoneSide, phone.Id, [tablet]));
        var redraw = SwarmLeaderElection.ResolveRankCollision(tabletSide, tablet.Id, [phone]);

        Assert.NotNull(redraw);
        Assert.Equal(tablet.Id, redraw.Member);
        Assert.Equal(SwarmMembershipVerdict.Accepted,
            tabletSide.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(redraw), tabletKey)));
        var seen = phoneSide.Members.Concat(tabletSide.Members).DistinctBy(member => member.Id).ToArray();
        var lowest = seen.Min(member => member.Rank);
        Assert.Equal(phone.Id, Assert.Single(seen, member => member.Rank == lowest).Id);
        Assert.Null(SwarmLeaderElection.ResolveRankCollision(tabletSide, tablet.Id, [phone]));
    }
}
