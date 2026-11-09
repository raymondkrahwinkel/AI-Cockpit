using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot;

// The personal MCPs of the gateway that still need the operator; the UI part opens the browser, so the token stays in Depot.
internal sealed class DepotPersonalMcps(
    ICockpitHost host,
    DepotGateway gateway,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    ILogger? logger = null) : IDisposable
{
    // Fixed intervals from the ticket, no setting; change them here if they turn out wrong.
    private static readonly TimeSpan _pollEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan _pollFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _readEvery = TimeSpan.FromMinutes(15);

    private const string KeyPrefix = "depot.personal.";

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _announced = [];
    private readonly CancellationTokenSource _stop = new();
    private ICockpitHost _host = host;
    private bool _notify;
    private DepotPersonalTarget? _waiting;
    private CancellationTokenSource? _wait;

    // The first start builds this without a bell; Initialize hands over the live host and turns the bell on.
    public void Attach(ICockpitHost live)
    {
        _host = live;
        _notify = true;
    }

    public void Start() => _ = _ReadLoopAsync(_stop.Token);

    private async Task _ReadLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_readEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await Task.WhenAll(connections().Select(connection => ReadAsync(connection, cancellationToken))).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger?.LogWarning(exception, "Reading the personal MCPs failed; trying again next time.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Reads the discovery of one connection and acts on what changed.
    public async Task ReadAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken, bool announce = true)
    {
        try
        {
            var before = gateway.PersonalFor(connection);
            await gateway.RefreshAsync(connection, cancellationToken).ConfigureAwait(false);
            await _ActAsync(connection, before, announce, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogDebug(exception, "Reading the personal MCPs of {Connection} failed.", connection.Name);
        }
    }

    private async Task _ActAsync(DepotConnectionRegistration connection, IReadOnlyList<DepotPersonalMcp> before, bool announce, CancellationToken cancellationToken)
    {
        var after = gateway.PersonalFor(connection);
        if (before.Any(old => DepotPersonalStatus.NeedsAuthorization(old.Status) && after.Any(now => now.Id == old.Id && now.Status == DepotPersonalStatus.Active)))
        {
            await _host.RefreshMcpServerAsync(DepotGateway.ServerNameFor(connection), cancellationToken).ConfigureAwait(false);
        }

        if (!before.SequenceEqual(after))
        {
            _Publish(DepotPersonalChannel.Changed);
        }

        if (announce)
        {
            _Announce(connection);
        }
    }

    public async Task<DepotPersonalList> ListAsync(bool refresh, CancellationToken cancellationToken)
    {
        var all = connections();
        if (refresh)
        {
            await Task.WhenAll(all.Select(connection => ReadAsync(connection, cancellationToken))).ConfigureAwait(false);
        }

        lock (_lock)
        {
            return new([.. all.Select(connection => new DepotPersonalConnection(connection.Id, connection.Name, gateway.PersonalFor(connection)))], _waiting?.Id);
        }
    }

    // Starts the wait and returns the address to open; refused while another browser is waited for or when nothing is left to authorize.
    public DepotPersonalAuthorizeAnswer Authorize(DepotPersonalTarget target)
    {
        var connection = connections().FirstOrDefault(candidate => candidate.Id == target.ConnectionId);
        var mcp = connection is null ? null : gateway.PersonalFor(connection).FirstOrDefault(candidate => candidate.Id == target.Id);
        if (connection is null || mcp is null || !DepotPersonalStatus.NeedsAuthorization(mcp.Status))
        {
            return new(null, "That MCP does not need authorizing any more.");
        }

        CancellationTokenSource wait;
        lock (_lock)
        {
            if (_waiting is not null)
            {
                return new(null, "Another authorization is still waiting for its browser.");
            }

            _waiting = target;
            wait = _wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        }

        _Publish(DepotPersonalChannel.Changed);
        _ = _WaitAsync(connection, target, wait);
        return new(mcp.AuthorizeUrl);
    }

    // Frees the wait at once, so a new Authorize is not refused while the old poll winds down.
    public void Cancel()
    {
        lock (_lock)
        {
            _wait?.Cancel();
            _waiting = null;
            _wait = null;
        }

        _Publish(DepotPersonalChannel.Changed);
    }

    // Reads the discovery every 3 s for 5 minutes at most, until that MCP is Active or the wait is cancelled.
    private async Task _WaitAsync(DepotConnectionRegistration connection, DepotPersonalTarget target, CancellationTokenSource wait)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(wait.Token);
            timeout.CancelAfter(_pollFor);
            while (true)
            {
                await Task.Delay(_pollEvery, timeout.Token).ConfigureAwait(false);
                var before = gateway.PersonalFor(connection);
                await gateway.RefreshAddressAsync(connection, timeout.Token).ConfigureAwait(false);
                await _ActAsync(connection, before, announce: true, timeout.Token).ConfigureAwait(false);
                if (gateway.PersonalFor(connection).Any(mcp => mcp.Id == target.Id && mcp.Status == DepotPersonalStatus.Active))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Waiting for the authorization of a personal MCP of {Connection} failed.", connection.Name);
        }
        finally
        {
            lock (_lock)
            {
                // A Cancel and a new Authorize may have replaced this wait already.
                if (ReferenceEquals(_wait, wait))
                {
                    _waiting = null;
                    _wait = null;
                }
            }

            wait.Dispose();
            _Publish(DepotPersonalChannel.Changed);
        }
    }

    // One bell per connection, not per MCP, and only when what it says changed.
    private void _Announce(DepotConnectionRegistration connection)
    {
        var open = _Open(connection);
        lock (_lock)
        {
            if (open.Count == 0)
            {
                _announced.Remove(connection.Id);
                return;
            }

            var signature = _Signature(open);
            if (_announced.TryGetValue(connection.Id, out var last) && last == signature)
            {
                return;
            }

            _announced[connection.Id] = signature;
        }

        if (_notify)
        {
            _host.Notify(_NotificationFor(connection, open));
        }
    }

    private List<DepotPersonalMcp> _Open(DepotConnectionRegistration connection) =>
        [.. gateway.PersonalFor(connection).Where(mcp => DepotPersonalStatus.NeedsAuthorization(mcp.Status))];

    private static string _Signature(List<DepotPersonalMcp> open) => string.Join('|', open.Select(mcp => mcp.Id + mcp.Status));

    private PluginNotification _NotificationFor(DepotConnectionRegistration connection, List<DepotPersonalMcp> open)
    {
        // One MCP goes straight to its browser; several are chosen in the list, in the Depot settings.
        Action authorize = open.Count == 1
            ? () => _Publish(DepotPersonalChannel.AuthorizeRequested, new DepotPersonalTarget(connection.Id, open[0].Id))
            : () => _Publish(DepotChannel.OpenSettings, true);
        return new(
            KeyPrefix + connection.Id,
            open.Count == 1 ? $"{open[0].Name} needs authorizing. Its tools stay empty until then." : $"{open.Count} personal MCPs need authorizing. Their tools stay empty until then.",
            PluginToastSeverity.Warning,
            [new PluginToastAction("Authorize", authorize)])
        {
            Title = $"Depot: {connection.Name}",
        };
    }

    // The host's startup re-check of a kept bell: the answer is live, or null when it was resolved meanwhile. The bell is
    // then already in the notification center, so it is remembered as announced and a later read does not ring it again.
    public async Task<PluginNotification?> RecheckAsync(string key)
    {
        var connection = key.StartsWith(KeyPrefix, StringComparison.Ordinal)
            ? connections().FirstOrDefault(candidate => candidate.Id == key[KeyPrefix.Length..])
            : null;
        if (connection is null)
        {
            return null;
        }

        await ReadAsync(connection, _stop.Token, announce: false).ConfigureAwait(false);
        var open = _Open(connection);
        if (open.Count == 0)
        {
            return null;
        }

        lock (_lock)
        {
            _announced[connection.Id] = _Signature(open);
        }

        return _NotificationFor(connection, open);
    }

    // A channel that is gone (shutdown) must not take a fire-and-forget task down with it.
    private void _Publish<T>(string name, T payload)
    {
        try
        {
            _host.Channel.Publish(name, JsonSerializer.SerializeToElement(payload, DepotChannel.Json));
        }
        catch (Exception exception)
        {
            logger?.LogDebug(exception, "Publishing {Event} failed.", name);
        }
    }

    private void _Publish(string name) => _Publish(name, true);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
