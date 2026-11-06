using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Cockpit.Plugin.Browser.Contracts;

namespace Cockpit.Plugin.Browser;

internal sealed class AddressPolicy(Func<IReadOnlyList<string>> allowlist,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
    Func<IEnumerable<IPAddress>>? ownMachineAddresses = null)
{
    public async Task<string?> BlockReasonAsync(Uri uri, CancellationToken token)
        => (await ResolveAsync(uri, token).ConfigureAwait(false)).Reason;

    internal bool IsOwnMachineOnly(IEnumerable<IPAddress> addresses) =>
        addresses.Any() && addresses.All(IsOwnMachineAddress);

    public async Task<(IPAddress[] Addresses, string? Reason)> ResolveAsync(Uri uri, CancellationToken token)
    {
        var entries = allowlist();
        var allowedHost = entries.Any(entry => NetworkAllowlist.AllowsHost(entry, uri));
        var host = NetworkAllowlist.Host(uri);
        string? nameCategory = null;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            nameCategory = "localhost";
        }
        if (host.Equals("local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            nameCategory = ".local";
        }
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal]
                : await (resolve ?? Dns.GetHostAddressesAsync)(host, token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return ([], $"Network access blocked: {nameCategory ?? "DNS resolution failed"}.");
        }
        if (addresses.Length == 0)
        {
            return ([], "Network access blocked: DNS returned no addresses.");
        }
        var normalized = addresses.Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToArray();
        var literalOwnMachineAddress = IPAddress.TryParse(host, out var ownMachineLiteral) && IsOwnMachineAddress(Normalize(ownMachineLiteral));
        if (nameCategory == "localhost" && normalized.All(IsLoopbackAddress)
            || literalOwnMachineAddress && IsOwnMachineOnly(normalized))
        {
            return (normalized, null);
        }
        if (nameCategory == ".local" && IsOwnMachineOnly(normalized))
        {
            return (normalized, null);
        }
        foreach (var address in normalized)
        {
            if (allowedHost || entries.Any(entry => NetworkAllowlist.AllowsAddress(entry, address)))
            {
                continue;
            }
            if (nameCategory is not null)
            {
                return ([], $"Network access blocked: {nameCategory} name.");
            }
            if (Category(address) is { } category)
            {
                return ([], $"Network access blocked: {category} address ({address}).");
            }
        }
        return (addresses, null);
    }

    private bool IsOwnMachineAddress(IPAddress address) =>
        IsLoopbackAddress(address) || OwnMachineAddresses().Any(own => Normalize(own).Equals(address));

    private IEnumerable<IPAddress> OwnMachineAddresses() => ownMachineAddresses?.Invoke()
        ?? NetworkInterface.GetAllNetworkInterfaces().SelectMany(network => network.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsLoopbackAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? address.GetAddressBytes()[0] == 127 : IPAddress.IPv6Loopback.Equals(address);

    private static string? Category(IPAddress address)
    {
        if (IsLoopbackAddress(address))
        {
            return "loopback";
        }
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes switch
            {
                [10, ..] or [172, >= 16 and <= 31, ..] or [192, 168, ..] => "RFC1918",
                [169, 254, ..] => "link-local",
                [100, >= 64 and <= 127, ..] => "CGNAT",
                [0, ..] => "unspecified",
                _ => null,
            };
        }
        if (address.Equals(IPAddress.IPv6Any))
        {
            return "unspecified";
        }
        if (address.IsIPv6LinkLocal)
        {
            return "link-local";
        }
        return (bytes[0] & 0xfe) == 0xfc ? "unique-local" : null;
    }
}
