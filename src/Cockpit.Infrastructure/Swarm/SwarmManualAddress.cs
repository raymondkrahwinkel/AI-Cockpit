using System.Net;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmManualAddress(SwarmSettingsStore settings, SwarmNetworkPolicy network) : ISingletonService
{
    public static Uri Parse(string address)
    {
        var value = address.Trim();
        if (IPAddress.TryParse(value, out var ip)) value = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
        var uri = NodePairingClient.NormalizeAddress(value);
        if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Port == 0)
            throw new FormatException("Enter a host or host:port.");
        var authority = value.Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var explicitPort = authority.StartsWith('[') ? authority.Contains("]:", StringComparison.Ordinal) : authority.Contains(':');
        return explicitPort ? uri : new UriBuilder(uri) { Port = 20382 }.Uri;
    }

    public async Task<Uri> ResolveAsync(string address, CancellationToken ct = default)
    {
        var uri = Parse(address);
        await network.ResolveInternalAsync(uri.IdnHost, ct);
        return uri;
    }

    public async Task<Uri?> ForMemberAsync(SwarmMemberId member, CancellationToken ct = default)
    {
        var entry = (await settings.LoadAsync(ct)).ManualAddresses.FirstOrDefault(item => item.MemberId == member.Value);
        return entry is null ? null : Parse(entry.Address);
    }

    internal static string Key(Uri uri) => uri.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped);

    internal async Task AttachAsync(string address, SwarmMemberId member, CancellationToken ct)
    {
        if ((await settings.LoadAsync(ct)).ManualAddresses.Any(item => item.Address == Key(Parse(address))))
            await RememberAsync(address, member.Value, online: true, ct: ct);
    }

    public async Task RememberAsync(string address, string? memberId = null, Exception? failure = null, bool online = false, CancellationToken ct = default)
    {
        var uri = Parse(address);
        var error = failure is null ? null : NodeConnectionFailure.Find<NodePairingException>(failure);
        var pin = failure is null ? null : NodeConnectionFailure.Find<NodeCertificatePinMismatchException>(failure);
        var refused = failure is null ? null : NodeConnectionFailure.Find<System.Net.Sockets.SocketException>(failure);
        var code = error?.Error ?? (pin is not null ? "certificate_changed" : refused?.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused
            ? "connection_refused" : failure is not null ? "no_answer" : "");
        var status = online ? "Online" : failure is null ? "Awaiting pairing" : code switch
        {
            "certificate_changed" => "Certificate changed",
            "connection_refused" => "Connection refused",
            SwarmNetworkError.AddressNotInternal => "Not reachable",
            _ => "No answer",
        };
        var message = failure is null ? "" : error?.Message ?? NodeConnectionFailure.Describe(uri.Authority, failure, NodeConnectionFailure.DefaultBudget);
        var previous = (await settings.LoadAsync(ct)).ManualAddresses.FirstOrDefault(item => item.Address == Key(uri));
        var entry = new SwarmManualMember(Key(uri), memberId ?? previous?.MemberId, status, code, message);
        if (entry == previous) return;
        await settings.UpdateAsync(current =>
        {
            var latest = current.ManualAddresses.FirstOrDefault(item => item.Address == entry.Address);
            return current with { ManualAddresses = [.. current.ManualAddresses.Where(item => item.Address != entry.Address), entry with { MemberId = memberId ?? latest?.MemberId }] };
        }, ct);
    }
}
