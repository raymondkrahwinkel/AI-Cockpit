using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Cockpit.Infrastructure.Mcp;

// AC-790: best-effort LAN-facing address for the node UI — reachable from another machine, not 127.0.0.1 or a
// wildcard bind address. ponytail: name/type heuristics, not a routing-table lookup, can still pick the wrong
// NIC on unusual networking. Upgrade path: an operator-driven interface picker, once that actually bites.
internal static class NodeReachableAddress
{
    private static readonly string[] _VirtualNameHints =
        ["docker", "veth", "br-", "vmnet", "vboxnet", "virbr", "wsl", "tailscale", "zerotier"];

    public static string? Resolve() =>
        RealUnicastAddresses()
            // A real Ethernet/Wi-Fi adapter first; anything else (PPP, other) only if nothing better exists.
            .OrderBy(candidate => candidate.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .Select(candidate => candidate.Address.Address.ToString())
            .FirstOrDefault();

    // AC-793: every IPv4 unicast address on a real-looking LAN interface, loopback/tunnels/virtual adapters
    // filtered out. Shared with NodeVisibilityPolicy so "reach me at" and "my own network" can't disagree.
    internal static IEnumerable<(NetworkInterfaceType Type, UnicastIPAddressInformation Address)> RealUnicastAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && !_VirtualNameHints.Any(hint =>
                    nic.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses.Select(address => (nic.NetworkInterfaceType, Address: address)))
            .Where(candidate => candidate.Address.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(candidate.Address.Address)
                && !_IsLinkLocal(candidate.Address.Address));

    private static bool _IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
