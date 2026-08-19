using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

// An in-memory `IPluginStorage` for exercising `GitHubIssuesSettings` without the host's real per-plugin store.
internal sealed class InMemoryPluginStorage : IPluginStorage
{
    private readonly Dictionary<string, object?> _store = new(StringComparer.Ordinal);

    public T? Get<T>(string key) => _store.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public void Set<T>(string key, T value) => _store[key] = value;
}

// A `ICockpitSessionObserver` whose active pane, working directory and per-pane current-turn images the test sets directly (AC-116).
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

// A minimal `ICockpitHost` that supplies a `FakeSessionObserver` and records toasts; unused members throw so a test that reaches one is caught.
internal sealed class FakeCockpitHost : ICockpitHost
{
    private TaskCompletionSource? _openDialog;

    public FakeSessionObserver Observer { get; } = new();

    public FakeCockpitActions FakeActions { get; } = new();

    public List<string> Toasts { get; } = [];

    // How many times the New-session dialog was asked for — what proves a second click cannot open a second one.
    public int NewSessionDialogsOpened { get; private set; }

    // What the last New-session request asked the dialog to open with — the fields the operator is shown.
    public NewSessionPrefill? LastPrefill { get; private set; }

    // The callbacks the last New-session request handed over, so a test can play the operator pressing Start or Cancel.
    public Action<string>? OnSessionStarted { get; private set; }

    public Action? OnSessionCancelled { get; private set; }

    // What the markdown seam should throw instead of rendering, if anything. A `MissingMethodException`
    // stands in for a cockpit older than this plugin's `minHostVersion` — one whose contract has no
    // `CreateMarkdownView`, so the call the plugin compiled against finds no method to bind to. Any other
    // exception stands in for the rest of the ways rendering a body can fail (AC-304).
    public Exception? MarkdownFailure { get; set; }

    // What the operator linked the project to, keyed by project-field key — what the host would have stored from the project editor.
    public Dictionary<string, string> ProjectFieldValues { get; } = new(StringComparer.Ordinal);

    // The pane each `GetProjectFieldValueAsync` call asked about, so a test can prove a contribution asks about its own session rather than whichever pane is selected.
    public List<string?> ProjectFieldPanesAsked { get; } = [];

    public IServiceProvider Services => throw new NotSupportedException();

    public ICockpitActions Actions => FakeActions;

    public IPluginStorage Storage => throw new NotSupportedException();

    public ICockpitSessionObserver Sessions => Observer;

    // The returned task completes when the dialog closes, exactly as the host's own does — a fake that completed it
    // straight away would let a caller look like it never held the dialog open at all.
    public Task ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, Action<string>? onStarted = null, Action? onCancelled = null)
    {
        NewSessionDialogsOpened++;
        LastPrefill = prefill;
        OnSessionStarted = onStarted;
        OnSessionCancelled = onCancelled;
        _openDialog = new TaskCompletionSource();
        return _openDialog.Task;
    }

    // Plays the operator closing the New-session dialog, which is what completes the task the caller awaited.
    public void CloseNewSessionDialog() => _openDialog?.TrySetResult();

    public Task<string?> GetProjectFieldValueAsync(string key, string? paneId = null, CancellationToken cancellationToken = default)
    {
        ProjectFieldPanesAsked.Add(paneId);
        return Task.FromResult(ProjectFieldValues.TryGetValue(key, out var value) ? value : null);
    }

    // AC-940: `ProjectFieldValues` stores the raw comma-separated value exactly as the real host's project store
    // would — split here the same simple way the host's own `ProjectLinkValues.Split` does, so a test that sets
    // "a/b, c/d" sees both repositories back.
    public Task<IReadOnlyList<string>> GetProjectFieldValuesAsync(string key, string? paneId = null, CancellationToken cancellationToken = default)
    {
        ProjectFieldPanesAsked.Add(paneId);
        return Task.FromResult<IReadOnlyList<string>>(
            ProjectFieldValues.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? [.. value.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0)]
                : []);
    }

    public void AddSettings(Func<Control> createView) => throw new NotSupportedException();

    public void AddSideMenuButton(string title, Action onInvoke) => throw new NotSupportedException();

    public void AddSideMenuSection(string title, Func<Control> createView) => throw new NotSupportedException();

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        throw new NotSupportedException();

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        Toasts.Add(message);

    public Control CreateMarkdownView(string markdown) => MarkdownFailure is { } failure
        ? throw failure
        : new SelectableTextBlock { Text = markdown, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    // The statusline each pane was last given (#AC-310) — an empty string is a pane whose line was cleared.
    public Dictionary<string, string> Statuslines { get; } = new(StringComparer.Ordinal);

    // The names proposed per pane. Kept apart from a name the plugin would have *set*: only the host decides whether a suggestion is taken, and the plugin must never be the one to overrule the operator.
    public Dictionary<string, string> SuggestedNames { get; } = new(StringComparer.Ordinal);

    public Task SetSessionStatusline(string paneId, string statusline)
    {
        Statuslines[paneId] = statusline;
        return Task.CompletedTask;
    }

    public Task SetSessionName(string paneId, string name) =>
        throw new NotSupportedException("Linking must suggest a name, never take one.");

    public Task SuggestSessionName(string paneId, string name)
    {
        SuggestedNames[paneId] = name;
        return Task.CompletedTask;
    }
}
