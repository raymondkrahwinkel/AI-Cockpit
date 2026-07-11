using System.Threading.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// A hand-written <see cref="IPluginSessionDriver"/> test double (#45) — records every call it receives and
/// lets a test push <see cref="PluginSessionEvent"/>s onto <see cref="Events"/> on demand, standing in for a
/// real plugin's driver in <see cref="PluginSessionDriverAdapterTests"/> and <see cref="SessionDriverFactoryTests"/>.
/// </summary>
internal sealed class FakePluginSessionDriver : IPluginSessionDriver
{
    private readonly Channel<PluginSessionEvent> _events = Channel.CreateUnbounded<PluginSessionEvent>();

    public PluginSessionCapabilities Capabilities { get; init; } = new(true, true);

    public string? SessionId { get; set; }

    public List<string> SentMessages { get; } = [];

    public string? LastModel { get; private set; }

    public bool Started { get; private set; }

    public bool Interrupted { get; private set; }

    public (string ToolUseId, bool Allow)? LastPermissionResponse { get; private set; }

    public bool? LastAutoApprove { get; private set; }

    public bool Disposed { get; private set; }

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        Started = true;
        LastModel = model;
        return Task.CompletedTask;
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(text);
        return Task.CompletedTask;
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        Interrupted = true;
        return Task.CompletedTask;
    }

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default)
    {
        LastPermissionResponse = (toolUseId, allow);
        return Task.CompletedTask;
    }

    public Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        LastAutoApprove = enabled;
        return Task.CompletedTask;
    }

    public void Emit(PluginSessionEvent pluginEvent) => _events.Writer.TryWrite(pluginEvent);

    public void Complete() => _events.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
