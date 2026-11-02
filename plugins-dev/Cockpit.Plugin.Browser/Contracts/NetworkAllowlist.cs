using System.Globalization;
using System.Net;

namespace Cockpit.Plugin.Browser.Contracts;

internal static class NetworkAllowlist
{
    public static bool IsValid(string entry) => TryNetwork(entry, out _) || TryHost(entry, out _, out _);

    public static bool IsNetwork(string entry) => TryNetwork(entry, out _);

    public static bool AllowsHost(string entry, Uri uri) =>
        TryHost(entry, out var host, out var port)
        && host.Equals(Host(uri), StringComparison.OrdinalIgnoreCase)
        && (port is null || port == uri.Port);

    public static bool AllowsAddress(string entry, IPAddress address) =>
        TryNetwork(entry, out var network) && network.Contains(address);

    public static string Host(Uri uri) => uri.IdnHost.Trim('[', ']').TrimEnd('.');

    private static bool TryNetwork(string entry, out IPNetwork network) => IPNetwork.TryParse(entry, out network);

    private static bool TryHost(string entry, out string host, out int? port)
    {
        host = "";
        port = null;
        if (string.IsNullOrWhiteSpace(entry) || entry != entry.Trim() || entry.IndexOfAny(['/', '?', '#', '@', '\\', '*']) >= 0)
        {
            return false;
        }
        if (IPAddress.TryParse(entry.Trim('[', ']'), out var address))
        {
            host = address.ToString();
            return true;
        }
        var separator = entry.LastIndexOf(':');
        if (separator >= 0)
        {
            if (!int.TryParse(entry.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) || parsedPort is < 1 or > 65535)
            {
                return false;
            }
            port = parsedPort;
        }
        var name = separator >= 0 ? entry[..separator] : entry;
        if (Uri.CheckHostName(name.Trim('[', ']')) == UriHostNameType.Unknown
            || !Uri.TryCreate("http://" + entry, UriKind.Absolute, out var uri))
        {
            return false;
        }
        host = Host(uri);
        return true;
    }
}
