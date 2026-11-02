using System.Net;
using System.Net.Sockets;
using Cockpit.Plugin.Browser.Contracts;

namespace Cockpit.Plugin.Browser;

internal sealed class AddressPolicy(Func<IReadOnlyList<string>> allowlist,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
{
    public async Task<string?> BlockReasonAsync(Uri uri, CancellationToken token)
    {
        var entries = allowlist();
        if (entries.Any(entry => NetworkAllowlist.AllowsHost(entry, uri)))
        {
            return null;
        }
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
        if (nameCategory is not null && !entries.Any(NetworkAllowlist.IsNetwork))
        {
            return $"Network access blocked: {nameCategory} name.";
        }
        IPAddress[] addresses;
        try
        {
            // AC-1883: Chromium resolves again when connecting; DNS rebinding between these lookups remains possible.
            addresses = IPAddress.TryParse(host, out var literal) ? [literal]
                : await (resolve ?? Dns.GetHostAddressesAsync)(host, token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return $"Network access blocked: {nameCategory ?? "DNS resolution failed"}.";
        }
        if (addresses.Length == 0)
        {
            return "Network access blocked: DNS returned no addresses.";
        }
        foreach (var resolved in addresses)
        {
            var address = resolved.IsIPv4MappedToIPv6 ? resolved.MapToIPv4() : resolved;
            if (entries.Any(entry => NetworkAllowlist.AllowsAddress(entry, address)))
            {
                continue;
            }
            if (nameCategory is not null)
            {
                return $"Network access blocked: {nameCategory} name.";
            }
            if (Category(address) is { } category)
            {
                return $"Network access blocked: {category} address ({address}).";
            }
        }
        return null;
    }

    private static string? Category(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
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
