using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>An in-memory <see cref="IPluginStorage"/> for exercising <see cref="YouTrackSettings"/> without the host's real per-plugin store.</summary>
internal sealed class InMemoryPluginStorage : IPluginStorage
{
    private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

    public T? Get<T>(string key) => _store.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public void Set<T>(string key, T value) => _store[key] = value;
}

/// <summary>A <see cref="ICockpitSessionObserver"/> whose active pane and per-pane current-turn images the test sets directly (AC-116).</summary>
internal sealed class FakeSessionObserver : ICockpitSessionObserver
{
    public string? ActiveSessionWorkingDirectory => null;

    public string? ActivePaneId { get; set; }

    public Dictionary<string, IReadOnlyList<SessionImageAttachment>> ImagesByPane { get; } = new(StringComparer.Ordinal);

    public event EventHandler? ActiveSessionChanged { add { } remove { } }

    public event EventHandler<SessionOutputText>? OutputProduced { add { } remove { } }

    public event EventHandler<SessionToolActivity>? ToolActivityObserved { add { } remove { } }

    public IReadOnlyList<SessionImageAttachment> GetCurrentTurnImages(string paneId) =>
        ImagesByPane.TryGetValue(paneId, out var images) ? images : [];
}

/// <summary>A minimal <see cref="ICockpitHost"/> that supplies a <see cref="FakeSessionObserver"/> and records toasts; unused members throw so a test that reaches one is caught.</summary>
internal sealed class FakeCockpitHost : ICockpitHost
{
    public FakeSessionObserver Observer { get; } = new();

    public List<string> Toasts { get; } = [];

    public IServiceProvider Services => throw new NotSupportedException();

    public ICockpitActions Actions => throw new NotSupportedException();

    public IPluginStorage Storage => throw new NotSupportedException();

    public ICockpitSessionObserver Sessions => Observer;

    public void AddSettings(Func<Control> createView) => throw new NotSupportedException();

    public void AddSideMenuButton(string title, Action onInvoke) => throw new NotSupportedException();

    public void AddSideMenuSection(string title, Func<Control> createView) => throw new NotSupportedException();

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        throw new NotSupportedException();

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        Toasts.Add(message);
}
