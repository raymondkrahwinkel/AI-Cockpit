using System.Threading.Channels;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Mcp;

internal sealed class GatewayMcpBinding : IAsyncDisposable
{
    private readonly McpToolProvider _provider;
    private readonly SessionMcpMounts _mounts;
    private readonly string _paneId;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _changes = Channel.CreateUnbounded<bool>();
    private readonly Task _worker;
    private readonly TaskCompletionSource _initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> _desired = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _connections = [];
    private Task _initialWork = Task.CompletedTask;
    private bool _started;

    public GatewayMcpBinding(McpToolProvider provider, SessionMcpMounts mounts, string paneId, string token)
    {
        _provider = provider;
        _mounts = mounts;
        _paneId = paneId;
        Session = provider.CreateGatewaySession(paneId, token, mounts.Set(paneId)?.Context);
        _mounts.Changed += _OnChanged;
        _changes.Writer.TryWrite(true);
        _worker = _RunAsync();
    }

    public IMcpToolSession Session { get; }
    public Task Initial => _initial.Task;

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
            await foreach (var _ in _changes.Reader.ReadAllAsync(_lifetime.Token))
            {
                var registry = await _provider.GatewayServersAsync(_paneId, _lifetime.Token);
                var desired = registry.Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var name in _desired)
                {
                    if (!desired.Contains(name))
                    {
                        Session.RemoveServer(name);
                    }
                }
                _desired.Clear();
                _desired.UnionWith(desired);
                var connecting = registry.Select(server => Session.AddServerAsync(server)).ToArray();
                _connections.RemoveAll(task => task.IsCompletedSuccessfully);
                _connections.AddRange(connecting);
                if (!_started)
                {
                    _started = true;
                    _initialWork = _CompleteInitialAsync(connecting);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _initial.TrySetResult();
        }
    }

    private async Task _CompleteInitialAsync(Task[] connecting)
    {
        await Task.WhenAll(connecting);
        _initial.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _mounts.Changed -= _OnChanged;
        await _lifetime.CancelAsync();
        await _worker;
        await Session.DisposeAsync();
        await Task.WhenAll(_connections);
        await _initialWork;
        _lifetime.Dispose();
    }
}
