using System.Net;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-793: the one place "own range, or an explicit whitelist, never open by default" gets decided, called
// through by both NodeDiscoveryResponder and NodePairingHost. Own range wins first; past it, only
// AllowedDiscoveryRanges. ponytail: leans on NodeReachableAddress's cosmetic name-hint filter as a trust boundary.
internal sealed class NodeVisibilityPolicy : INodeVisibilityPolicy, ISingletonService
{
    private readonly INodeEndpointSettingsStore _settings;
    private readonly Func<IEnumerable<IPNetwork>> _ownRanges;

    public NodeVisibilityPolicy(INodeEndpointSettingsStore settings)
        : this(settings, _DefaultOwnRanges)
    {
    }

    // Test seam: a fixed set of "own" ranges instead of this machine's real interfaces, so a range check can be
    // asserted in both directions without depending on what network the test happens to run on.
    internal NodeVisibilityPolicy(INodeEndpointSettingsStore settings, Func<IEnumerable<IPNetwork>> ownRanges)
    {
        _settings = settings;
        _ownRanges = ownRanges;
    }

    public async Task<bool> IsAllowedAsync(IPAddress caller, CancellationToken cancellationToken = default)
    {
        if (_ownRanges().Any(range => range.Contains(caller)))
        {
            return true;
        }

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return settings.AllowedDiscoveryRanges.Any(range => IPNetwork.TryParse(range, out var network) && network.Contains(caller));
    }

    // Loopback counts as own range, since same-host setups and every pairing test reach the node that way.
    // PrefixLength == 0 is excluded on purpose — a misreported/mid-DHCP adapter can report "all of IPv4", and
    // treating that as own range would silently open the node to the whole internet.
    private static IEnumerable<IPNetwork> _DefaultOwnRanges() =>
        NodeReachableAddress.RealUnicastAddresses()
            .Where(candidate => candidate.Address.PrefixLength > 0)
            .Select(candidate => new IPNetwork(candidate.Address.Address, candidate.Address.PrefixLength))
            .Append(IPNetwork.Parse("127.0.0.0/8"));
}
