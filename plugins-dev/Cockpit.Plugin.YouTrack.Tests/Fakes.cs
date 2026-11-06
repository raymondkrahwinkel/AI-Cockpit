using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.YouTrack.Tests;

// An in-memory `IPluginStorage` for exercising `YouTrackSettings` without the host's real per-plugin store.
internal sealed class InMemoryPluginStorage : IPluginStorage
{
    private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

    public T? Get<T>(string key) => _store.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public void Set<T>(string key, T value) => _store[key] = value;
}

// The caller's working directory stays independent of the active pane (AC-1397).
internal sealed class FakeSessionObserver : ICockpitSessionObserver
{
    public string? ActiveSessionWorkingDirectory { get; set; }

    public string? ActivePaneId { get; set; }

    public event EventHandler? ActiveSessionChanged { add { } remove { } }

    public event EventHandler<SessionOutputText>? OutputProduced { add { } remove { } }

    public event EventHandler<SessionToolActivity>? ToolActivityObserved { add { } remove { } }

    public Dictionary<string, string?> WorkingDirectories { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<SessionImageAttachment> GetCurrentTurnImages(string paneId) => [];

    public string? GetWorkingDirectory(string paneId) => WorkingDirectories.GetValueOrDefault(paneId);
}

internal sealed class FakeCockpitHost : ICockpitHost
{
    public FakeSessionObserver Observer { get; } = new();

    public List<string> Toasts { get; } = [];

    public IServiceProvider Services => throw new NotSupportedException();

    public ICockpitActions Actions => throw new NotSupportedException();

    public IPluginStorage Storage => throw new NotSupportedException();

    public ICockpitSessionObserver Sessions => Observer;

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        Toasts.Add(message);
}
