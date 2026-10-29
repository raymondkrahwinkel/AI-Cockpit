using System.Net;

namespace Cockpit.Core.Swarm;

// AC-1916: what the swarm counts as its own network. A swarm member only talks to addresses inside these ranges or
// inside the extra ones the operator configured; everything else is outside, with no switch to turn that off.
public static class InternalNetworkRanges
{
    // RFC 1918, IPv4 link-local, IPv6 link-local, IPv6 ULA, and loopback for both families.
    public static IReadOnlyList<IPNetwork> BuiltIn { get; } =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("::1/128"),
    ];

    // An IPv4-mapped IPv6 address (::ffff:a.b.c.d, how a dual-stack socket reports an IPv4 peer) is judged as the
    // IPv4 address it carries. .NET 10's IPNetwork.Contains already does so for an IPv4 range; mapping here first
    // keeps the answer from depending on that.
    public static bool IsInternal(IPAddress address, IEnumerable<IPNetwork> extraRanges)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return BuiltIn.Concat(extraRanges).Any(range => range.Contains(normalized));
    }

    // The operator's extra ranges as networks. An entry that is not a CIDR, or one with prefix length 0 (all of
    // IPv4 or all of IPv6), is skipped: either would otherwise be a way to switch the filter off.
    public static IReadOnlyList<IPNetwork> ParseExtra(IEnumerable<string> cidrs) =>
    [
        .. cidrs
            .Select(cidr => IPNetwork.TryParse(cidr.Trim(), out var network) ? network : (IPNetwork?)null)
            .OfType<IPNetwork>()
            .Where(network => network.PrefixLength > 0),
    ];
}

// The refusal codes of the swarm's network filter, one per place it holds.
public static class SwarmNetworkError
{
    // An address the operator typed, or a name that resolved, lies outside the internal ranges.
    public const string AddressNotInternal = "address_not_internal";

    // An incoming swarm call came from outside the internal ranges.
    public const string CallerNotInternal = "caller_not_internal";

    // A member announced an address outside the internal ranges; its last internal address is kept.
    public const string AddressUpdateIgnored = "address_update_ignored";
}
