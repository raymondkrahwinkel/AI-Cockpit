using System.Threading.Channels;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Mcp;

internal sealed class GatewayMcpBinding : IAsyncDisposable
{
    private readonly McpToolProvider _provider;
    private readonly SessionMcpMounts _mounts;
    private readonly string _paneId;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _changes = Channel.CreateUnbounded<bool>();
    private readonly Task _worker;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _pendingConnections = Task.CompletedTask;
    private readonly McpBackgroundTasks _connections;
    private int _disposeStarted;
    private SessionMcpSet? _appliedSet;

    public GatewayMcpBinding(McpToolProvider provider, SessionMcpMounts mounts, string paneId, string token, ILogger logger)
    {
        _provider = provider;
        _mounts = mounts;
        _paneId = paneId;
        _logger = logger;
        _connections = new(logger);
        Session = provider.CreateGatewaySession(paneId, token, mounts.Set(paneId)?.Context);
        _mounts.Changed += _OnChanged;
        _changes.Writer.TryWrite(true);
        _worker = _RunAsync();
    }

    public IMcpToolSession Session { get; }

    public async Task WaitForConnectionsAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await _ready.Task.WaitAsync(deadline.Token);
            await Session.WaitForConnectionsAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The first tool list goes out without the servers still connecting; they arrive later through list_changed.
            _logger.LogWarning("Pane {Pane} did not get all its MCP servers within 3 s.", _paneId);
        }
    }

    private void _OnChanged(string paneId)
    {
        if (paneId == _paneId)
        {
            _changes.Writer.TryWrite(true);
        }
    }

    private async Task _RunAsync()
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(_lifetime.Token))
            {
                while (_changes.Reader.TryRead(out _)) { }
                try
                {
                    var set = _mounts.Set(_paneId);
                    // Connection reports raise Changed too; only a new desired set invalidates this cache (AC-2310).
                    if (ReferenceEquals(set, _appliedSet))
                    {
                        continue;
                    }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var registry = await _provider.GatewayServersAsync(_paneId, deadline.Token);
                    _pendingConnections = Session.UpdateServersAsync(registry);
                    _appliedSet = set;
                    _ready.TrySetResult();
                    var pending = _pendingConnections;
                    _connections.Run(() => pending);
                }
                catch (Exception) when (!_lifetime.IsCancellationRequested)
                {
                    _logger.LogWarning("MCP gateway catalog refresh failed; retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
                    _changes.Writer.TryWrite(true);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }
        _mounts.Changed -= _OnChanged;
        await _lifetime.CancelAsync();
        await _worker;
        await Session.DisposeAsync();
        await _pendingConnections;
        await _connections.DrainAsync();
        _lifetime.Dispose();
    }
}
