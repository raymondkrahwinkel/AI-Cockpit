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

    public int? ProcessId { get; set; }

    public PluginSessionStatus? Status { get; set; }

    public IReadOnlyList<PluginSessionLaunchOption> LiveOptions { get; init; } = [];

    public (string Key, string Value)? LastLiveOption { get; private set; }

    public string? LastAllowAlwaysToolUseId { get; private set; }

    public List<string> SentMessages { get; } = [];

    public string? LastModel { get; private set; }

    public string? LastWorkingDirectory { get; private set; }

    public string? LastResumeSessionId { get; private set; }

    public IReadOnlyDictionary<string, string>? LastLaunchOptions { get; private set; }

    public IReadOnlyList<PluginMcpServer>? LastMcpServers { get; private set; }

    public IReadOnlyDictionary<string, string>? LastEnvironment { get; private set; }

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

    public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, CancellationToken cancellationToken)
    {
        LastWorkingDirectory = workingDirectory;
        LastResumeSessionId = resumeSessionId;
        LastLaunchOptions = options;
        LastMcpServers = mcpServers;
        return StartAsync(model, cancellationToken);
    }

    public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        LastEnvironment = environment;
        return StartAsync(model, workingDirectory, resumeSessionId, options, mcpServers, cancellationToken);
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

    public Task AllowPermissionAlwaysAsync(string toolUseId, CancellationToken cancellationToken = default)
    {
        LastAllowAlwaysToolUseId = toolUseId;
        return Task.CompletedTask;
    }

    public Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        LastAutoApprove = enabled;
        return Task.CompletedTask;
    }

    public List<(string Key, string Value)> LiveOptionSwitches { get; } = [];

    public Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        LastLiveOption = (key, value);
        LiveOptionSwitches.Add((key, value));
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
