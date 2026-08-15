using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Cockpit.Infrastructure.Mcp;

// Best-effort LAN-facing address for the node UI (AC-790): the operator types this into a second Cockpit, so it
// has to be something reachable from another machine, not "127.0.0.1" or a wildcard bind address.
// ponytail: name/type heuristics to skip obvious Docker/VPN/bridge adapters and link-local self-assigned
// addresses, not a routing-table lookup — on a machine with unusual networking (several real NICs, a VPN that
// owns the default route) this can still pick the wrong one. Upgrade path: let the operator pick/override the
// interface once that actually bites (no report of it yet, and pairing/discovery are later, sibling tickets).
internal static class NodeReachableAddress
{
    private static readonly string[] _VirtualNameHints =
        ["docker", "veth", "br-", "vmnet", "vboxnet", "virbr", "wsl", "tailscale", "zerotier"];

    public static string? Resolve() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && !_VirtualNameHints.Any(hint =>
                    nic.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses.Select(address => (nic.NetworkInterfaceType, address.Address)))
            .Where(candidate => candidate.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(candidate.Address)
                && !_IsLinkLocal(candidate.Address))
            // A real Ethernet/Wi-Fi adapter first; anything else (PPP, other) only if nothing better exists.
            .OrderBy(candidate => candidate.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .Select(candidate => candidate.Address.ToString())
            .FirstOrDefault();

    private static bool _IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
