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

/// <summary>A <see cref="ICockpitSessionObserver"/> whose active pane, working directory and per-pane current-turn images the test sets directly (AC-116).</summary>
internal sealed class FakeSessionObserver : ICockpitSessionObserver
{
    public string? ActiveSessionWorkingDirectory { get; set; }

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
    private TaskCompletionSource? _openDialog;

    public FakeSessionObserver Observer { get; } = new();

    public FakeCockpitActions FakeActions { get; } = new();

    public List<string> Toasts { get; } = [];

    /// <summary>How many times the New-session dialog was asked for — what proves a second click cannot open a second one.</summary>
    public int NewSessionDialogsOpened { get; private set; }

    /// <summary>The callbacks the last New-session request handed over, so a test can play the operator pressing Start or Cancel.</summary>
    public Action<string>? OnSessionStarted { get; private set; }

    public Action? OnSessionCancelled { get; private set; }

    public IServiceProvider Services => throw new NotSupportedException();

    public ICockpitActions Actions => FakeActions;

    public IPluginStorage Storage => throw new NotSupportedException();

    public ICockpitSessionObserver Sessions => Observer;

    // The returned task completes when the dialog closes, exactly as the host's own does — a fake that completed it
    // straight away would let a caller look like it never held the dialog open at all.
    public Task ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, Action<string>? onStarted = null, Action? onCancelled = null)
    {
        NewSessionDialogsOpened++;
        OnSessionStarted = onStarted;
        OnSessionCancelled = onCancelled;
        _openDialog = new TaskCompletionSource();
        return _openDialog.Task;
    }

    /// <summary>Plays the operator closing the New-session dialog, which is what completes the task the caller awaited.</summary>
    public void CloseNewSessionDialog() => _openDialog?.TrySetResult();

    public void AddSettings(Func<Control> createView) => throw new NotSupportedException();

    public void AddSideMenuButton(string title, Action onInvoke) => throw new NotSupportedException();

    public void AddSideMenuSection(string title, Func<Control> createView) => throw new NotSupportedException();

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        throw new NotSupportedException();

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        Toasts.Add(message);
}
