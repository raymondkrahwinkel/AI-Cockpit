using System.Net;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// The one place AC-793's "own range, or an explicit whitelist, never open by default" gets decided. Both
// `NodeDiscoveryResponder` (answering a query) and `NodePairingHost` (accepting `/pair/request`) call through
// here rather than each carrying its own copy of the range logic — that is what makes "checked at both moments"
// a property of the code instead of two call sites that have to be kept in sync by hand.
//
// Own range always wins, checked first and without touching disk: a node that could not find its own peers on
// its own LAN was never going to be useful, and that boundary does not need the operator's permission. Past it,
// only what `NodeEndpointSettings.AllowedDiscoveryRanges` explicitly lists — malformed entries are skipped
// rather than thrown on, because a single typo in the whitelist should not take down every check that follows it.
//
// ponytail: "own range" leans on `NodeReachableAddress`'s virtual-adapter name-hint filter, which was written to
// pick a display address (cosmetic) and is now also this policy's trust boundary (security) — its own comment
// already named "pairing/discovery are later, sibling tickets" as the reason to defer a real fix. A VPN or
// bridge type not on that hand-maintained list still reads as "own range" here. `PrefixLength == 0` is guarded
// against below because it is the one failure mode that turns "LAN only" into "the whole internet"; the
// narrower risk (a wrong adapter counted as local, not the entire address space) remains. Upgrade path: same as
// `NodeReachableAddress` — an operator-driven interface picker, once this actually bites.
//
// Assumes an IPv4-only caller throughout (`IPNetwork.Contains` on an IPv4-mapped IPv6 address like
// `::ffff:192.168.1.5` would not match an IPv4 own-range network). True today because `NodePairingHost` binds
// Kestrel on `IPAddress.Any` (IPv4 wildcard) rather than a dual-stack listener; would need revisiting if that
// ever changes.
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

    // Loopback included: a caller at 127.0.0.1 is this machine, which is "own range" in the most literal sense —
    // same-host controller/node setups and every existing pairing test reach the node exactly this way, over
    // loopback, and none of that should ever need a whitelist entry to keep working.
    //
    // `PrefixLength == 0` is excluded on purpose: no real interface's own subnet is legitimately "all of IPv4",
    // but a misreported or mid-DHCP adapter can hand back exactly that — and treating it as "own range" would
    // turn "own network only by default" into "open to the entire internet" for one bad NIC entry, silently and
    // for both the discovery reply and the pairing-request gate. Skipping the entry costs that one adapter's
    // subnet, not the whole check.
    private static IEnumerable<IPNetwork> _DefaultOwnRanges() =>
        NodeReachableAddress.RealUnicastAddresses()
            .Where(candidate => candidate.Address.PrefixLength > 0)
            .Select(candidate => new IPNetwork(candidate.Address.Address, candidate.Address.PrefixLength))
            .Append(IPNetwork.Parse("127.0.0.0/8"));
}
