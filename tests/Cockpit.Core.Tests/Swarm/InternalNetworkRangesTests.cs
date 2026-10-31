using System.Net;
using Cockpit.Core.Swarm;

namespace Cockpit.Core.Tests.Swarm;

/// <summary>
/// AC-1916 criterion 1 (layer 2, authorization): which addresses the swarm counts as its own network, including the
/// IPv4-mapped form a dual-stack socket reports and a VPN range the operator added.
/// </summary>
public class InternalNetworkRangesTests
{
    [Fact]
    public void IsInternal_HoldsOnlyTheInternalRanges_PlusTheConfiguredOnes()
    {
        var vpn = InternalNetworkRanges.ParseExtra(["100.64.0.0/10"]);
        bool IsInternal(string address, IEnumerable<IPNetwork> extra) => InternalNetworkRanges.IsInternal(IPAddress.Parse(address), extra);

        Assert.All(["8.8.8.8", "100.64.1.1", "2001:db8::1"], address => Assert.False(IsInternal(address, []), address));
        Assert.All(["192.168.1.5", "fd00::1", "fe80::1", "::ffff:10.0.0.1"], address => Assert.True(IsInternal(address, []), address));
        Assert.True(IsInternal("100.64.1.1", vpn));
    }
}
