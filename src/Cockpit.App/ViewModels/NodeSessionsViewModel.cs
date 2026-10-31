using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Remote;

namespace Cockpit.App.ViewModels;

// Here the separation is structural rather than typographic (AC-795, AC-561, AC-796).
public sealed partial class NodeSessionsViewModel(string nodeName) : ObservableObject, IDisposable
{
    private DateTimeOffset _connectedSince;
    private DateTimeOffset _lostAt;
    private DateTimeOffset _disconnectedAt;
    private long? _latencyMs;
    private string? _version;

    // AC-1456: a connect server's card shows its session-list group's connection instead of polling one of its own.
    private IRemoteServers? _servers;
    private IRemoteServer? _followed;

    public string NodeName { get; } = nodeName;

    // AC-1458: the row's address, for "Tailscale" or "network", and its key's expiry: stored at connect time and
    // reported by the server connection.
    public string? Url { get; init; }

    public DateTimeOffset? KeyExpiresAt { get; set; }

    public Action? EnterNewKey { get; init; }

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel), nameof(IsConnected), nameof(IsReconnecting), nameof(IsKeyExpired), nameof(IsDisconnected), nameof(CanDisconnect))]
    private NodeConnectionState _connectionState;

    [ObservableProperty]
    private string _connectionDetail = "";

    public string ConnectionLabel => ConnectionState switch
    {
        NodeConnectionState.Connected => "Connected",
        NodeConnectionState.Reconnecting => "Reconnecting",
        NodeConnectionState.KeyExpired => "Key expired",
        NodeConnectionState.Disconnected => "Disconnected",
        _ => "",
    };

    public bool IsConnected => ConnectionState == NodeConnectionState.Connected;

    public bool IsReconnecting => ConnectionState == NodeConnectionState.Reconnecting;

    public bool IsKeyExpired => ConnectionState == NodeConnectionState.KeyExpired;

    public bool IsDisconnected => ConnectionState == NodeConnectionState.Disconnected;

    public bool CanDisconnect => ConnectionState is NodeConnectionState.Connected or NodeConnectionState.Reconnecting;

    public void Dispose()
    {
        if (_servers is not null)
        {
            _servers.Changed -= _OnServersChanged;
        }

        if (_followed is not null)
        {
            _followed.StateChanged -= _OnServerStateChanged;
        }
    }

    // AC-1456: the group's stream is the one connection; Disconnect and Connect here are the group's own.
    public void FollowServer(IRemoteServers servers)
    {
        _servers = servers;
        servers.Changed += _OnServersChanged;
        Status = $"Its sessions are in the session list, under {NodeName}.";
        _ShowServer();
    }

    private void _OnServersChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_ShowServer);

    private void _OnServerStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_ShowServer);

    private void _ShowServer()
    {
        var server = _servers?.Servers.FirstOrDefault(candidate => string.Equals(candidate.Name, NodeName, StringComparison.Ordinal));
        if (!ReferenceEquals(server, _followed))
        {
            if (_followed is not null)
            {
                _followed.StateChanged -= _OnServerStateChanged;
            }

            _followed = server;
            if (server is not null)
            {
                server.StateChanged += _OnServerStateChanged;
            }
        }

        var now = DateTimeOffset.Now;
        var state = server?.State;
        var next = state switch
        {
            null => NodeConnectionState.Disconnected,
            { KeyRefused: true } => NodeConnectionState.KeyExpired,
            { IsConnected: true } => NodeConnectionState.Connected,
            _ => NodeConnectionState.Reconnecting,
        };
        if (next != ConnectionState)
        {
            _connectedSince = next == NodeConnectionState.Connected ? now : _connectedSince;
            _lostAt = next == NodeConnectionState.Reconnecting ? now : _lostAt;
            _disconnectedAt = next == NodeConnectionState.Disconnected ? now : _disconnectedAt;
        }

        _latencyMs = state?.LatencyMs;
        _version = state?.Key?.Version ?? _version;
        ConnectionState = next;
        _DescribeConnection();
    }

    // Disconnect the shared server connection; its sessions keep running.
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_servers is not null)
        {
            await _servers.DisconnectAsync(NodeName).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (_servers is not null)
        {
            await _servers.ReconnectAsync(NodeName).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void EnterKey() => EnterNewKey?.Invoke();

    private void _DescribeConnection() => ConnectionDetail = ConnectionState switch
    {
        NodeConnectionState.Connected =>
            $"since {_Clock(_connectedSince)} · {(_latencyMs is { } latency ? $"{latency} ms" : "no answer")} · {NetworkOf(Url)}{(_version is { Length: > 0 } version ? $" · v{version}" : "")}",
        NodeConnectionState.Reconnecting =>
            $"lost at {_Clock(_lostAt)} · sessions on the server keep running",
        NodeConnectionState.KeyExpired when KeyExpiresAt is { } expiresAt =>
            $"on {expiresAt.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture)} · reconnecting will not help",
        NodeConnectionState.Disconnected => $"by you at {_Clock(_disconnectedAt)}",
        _ => "",
    };

    private static string _Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    // "Tailscale" for an address in its 100.64.0.0/10 range or a MagicDNS name, "network" for anything else.
    public static string NetworkOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "network";
        }

        if (uri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return "Tailscale";
        }

        return IPAddress.TryParse(uri.Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            && ip.GetAddressBytes() is [100, var second, _, _] && (second & 0xC0) == 64
            ? "Tailscale"
            : "network";
    }
}

// One profile in the node's dropdown. Its own type rather than the wire record so the list can show the operator's
// note next to the label without the view reaching into a transport type.
public sealed record NodeProfileChoice(string Label, string? Purpose)
{
    public string Display => string.IsNullOrWhiteSpace(Purpose) ? Label : $"{Label} — {Purpose}";
}

// One project in the node's dropdown, or the "no project" row, whose `Id` is null.
public sealed record NodeProjectChoice(string? Id, string Name);

// Connection state reported by the shared server connection.
public enum NodeConnectionState
{
    Unknown,
    Connected,
    Reconnecting,
    KeyExpired,
    Disconnected,
}
