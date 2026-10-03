using System.Diagnostics;
using System.Net;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1456: one RemoteBackend per registry row that holds a connect key, so a pairing never becomes a server group.
// Nothing polls: the state follows the stream's own connection, and /whoami is asked once per (re)connect.
internal sealed class RemoteServers(IMcpServerStore registry, ILogger<RemoteServers> logger)
    : IRemoteServers, ISingletonService, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _reload = new(1, 1);
    private readonly HashSet<string> _disconnected = new(StringComparer.Ordinal);
    private IReadOnlySet<string> _known = new HashSet<string>(StringComparer.Ordinal);
    private bool _disposed;
    private IReadOnlyList<RemoteServer> _servers = [];

    public IReadOnlyList<IRemoteServer> Servers
    {
        get
        {
            lock (_gate)
            {
                return _servers;
            }
        }
    }

    public event EventHandler? Changed;

    public bool Knows(string name)
    {
        lock (_gate)
        {
            return _known.Contains(name);
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reload.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = (await registry.LoadAsync(cancellationToken).ConfigureAwait(false))
                .Select(row => (Row: row, Name: _ServerNameOf(row)))
                .Where(entry => entry.Name is not null)
                .ToList();

            List<RemoteServer> current;
            lock (_gate)
            {
                current = [.. _servers];
            }

            // A row whose address or key changed is a different connection: the old one goes, the new one comes.
            var wanted = rows.Where(entry => !_IsDisconnected(entry.Name ?? "")).ToList();
            var kept = current.Where(server => wanted.Any(entry => server.Matches(entry.Row))).ToList();
            var gone = current.Except(kept).ToList();
            var added = wanted
                .Where(entry => !kept.Any(server => server.Matches(entry.Row)))
                .Select(entry => new RemoteServer(entry.Name ?? "", entry.Row, logger))
                .ToList();

            // A reload still out when the cockpit shut down starts nothing, and lets go of what it made.
            bool disposed;
            lock (_gate)
            {
                disposed = _disposed;
                if (!disposed)
                {
                    _servers = [.. kept, .. added];
                    _known = rows.Select(entry => entry.Name ?? "").ToHashSet(StringComparer.Ordinal);
                }
            }

            if (disposed)
            {
                foreach (var server in added)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }

                return;
            }

            foreach (var server in gone)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var server in added)
            {
                server.Start();
            }

            if (gone.Count > 0 || added.Count > 0)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _reload.Release();
        }
    }

    public async Task DisconnectAsync(string name)
    {
        lock (_gate)
        {
            _disconnected.Add(name);
        }

        await ReloadAsync().ConfigureAwait(false);
    }

    public async Task ReconnectAsync(string name)
    {
        lock (_gate)
        {
            _disconnected.Remove(name);
        }

        await ReloadAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        List<RemoteServer> current;
        lock (_gate)
        {
            _disposed = true;
            current = [.. _servers];
            _servers = [];
        }

        foreach (var server in current)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool _IsDisconnected(string name)
    {
        lock (_gate)
        {
            return _disconnected.Contains(name);
        }
    }

    // A node's sessions row that carries a connect key and a pin; a pairing's shared secret has no "ck_".
    private static string? _ServerNameOf(McpServerConfig row) =>
        NodeServerName.Split(row.Name) is { } parts
            && string.Equals(parts.ServerName, NodeServerName.SessionsServerName, StringComparison.Ordinal)
            && row.ApiKey?.StartsWith(ConnectKeyVerifier.KeyPrefix, StringComparison.Ordinal) == true
            && !string.IsNullOrWhiteSpace(row.PinnedCertificateFingerprint)
            && Uri.TryCreate(row.Url, UriKind.Absolute, out var url)
            && url.Scheme == Uri.UriSchemeHttps
                ? parts.NodeName
                : null;
}

// AC-1456: one connection. It keeps trying until the server answers, then the stream keeps itself up; only a refused
// key stops it, and then the group says so instead of retrying a key that will not work.
internal sealed class RemoteServer : IRemoteServer, IAsyncDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly McpServerConfig _row;
    private readonly ILogger _logger;
    private readonly BackendApiClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private RemoteServerState _state = new(false, null, null);
    private RemoteBackend? _backend;
    private Task _connecting = Task.CompletedTask;

    // Counts the stream's opens and drops; read and written under _gate.
    private int _generation;

    public RemoteServer(string name, McpServerConfig row, ILogger logger)
    {
        Name = name;
        _row = row;
        _logger = logger;
        _client = new BackendApiClient(
            new Uri(new Uri(row.Url ?? ""), "/"),
            row.ApiKey ?? "",
            row.PinnedCertificateFingerprint ?? "",
            TimeProvider.System);
    }

    public string Name { get; }

    public ISessionRegistry? Sessions
    {
        get
        {
            lock (_gate)
            {
                return _backend;
            }
        }
    }

    public ISessionLauncher? Launcher => (ISessionLauncher?)Sessions;

    public RemoteServerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event EventHandler? StateChanged;

    public bool Matches(McpServerConfig row) =>
        string.Equals(row.Name, _row.Name, StringComparison.Ordinal)
        && string.Equals(row.Url, _row.Url, StringComparison.Ordinal)
        && string.Equals(row.ApiKey, _row.ApiKey, StringComparison.Ordinal)
        && string.Equals(row.PinnedCertificateFingerprint, _row.PinnedCertificateFingerprint, StringComparison.Ordinal);

    public void Start() => _connecting = _ConnectAsync();

    // Not waiting for a connect still out: a server that stalls must not hold up a Disconnect. That connect sees the
    // cancellation and lets its backend go.
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        RemoteBackend? backend;
        lock (_gate)
        {
            backend = _backend;
            _backend = null;
        }

        if (backend is not null)
        {
            await backend.DisposeAsync().ConfigureAwait(false);
        }

        _client.Dispose();
    }

    private async Task _ConnectAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _ReadKeyAsync().ConfigureAwait(false);
                // Connected is the stream's word, raised when its headers come back; this only hands the registry over.
                var backend = await RemoteBackend.ConnectAsync(_client, _OnConnection).ConfigureAwait(false);
                bool stopped;
                lock (_gate)
                {
                    stopped = _stop.IsCancellationRequested;
                    _backend = stopped ? null : backend;
                }

                if (stopped)
                {
                    await backend.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (BackendApiException exception) when (exception.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogInformation("Server {Server} refused this cockpit's connect key ({Status}).", Name, exception.Status);
                _Set(state => state with { KeyRefused = true });
                return;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogInformation(exception, "Server {Server} did not answer; trying again in {Backoff}.", Name, backoff);
            }

            try
            {
                await Task.Delay(backoff, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
        }
    }

    // Raised on the stream's thread. A drop shows at once; a return asks /whoami again for the latency and the key.
    private void _OnConnection(bool connected)
    {
        _logger.LogInformation("Server {Server}: event stream {State}.", Name, connected ? "open" : "lost");
        int generation;
        lock (_gate)
        {
            generation = ++_generation;
        }

        if (!connected)
        {
            var refused = Sessions is RemoteBackend { KeyRefused: true };
            _Set(state => state with { IsConnected = false, KeyRefused = state.KeyRefused || refused });
            return;
        }

        _ = _ReturnAsync(generation);
    }

    // Only the return of the connection still standing may say Connected; a /whoami that comes back after a later
    // drop belongs to a connection that is gone.
    private async Task _ReturnAsync(int generation)
    {
        try
        {
            await _ReadKeyAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Server {Server} reconnected but did not answer whoami.", Name);
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                _Set(state => generation == _generation ? state with { IsConnected = true } : state);
            }
        }
    }

    private async Task _ReadKeyAsync()
    {
        var started = Stopwatch.GetTimestamp();
        var who = await _client.WhoAmIAsync(_stop.Token).ConfigureAwait(false);
        var latency = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var key = new RemoteServerKey(who.Label, who.Capability, who.HoldsAssistant, who.AssistantHeldBy, who.Version, who.StartedAt, who.MayAnswerPermissions);
        _Set(state => state with { LatencyMs = latency, Key = key });
    }

    private void _Set(Func<RemoteServerState, RemoteServerState> change)
    {
        lock (_gate)
        {
            var next = change(_state);
            if (next == _state)
            {
                return;
            }

            _state = next;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
