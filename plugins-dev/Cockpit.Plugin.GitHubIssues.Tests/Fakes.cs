using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
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

// A minimal `ICockpitHost` for the backend part: it records what the plugin labels, asks and triggers; unused members
// throw so a test that reaches one is caught. AC-1396: the window-only members the dialog used moved to the UI host
// fake (UiHostFake), as the dialog no longer sees an ICockpitHost.
internal sealed class FakeCockpitHost : ICockpitHost
{
    // What the operator linked the project to, keyed by project-field key — what the host would have stored from the project editor.
    public Dictionary<string, string> ProjectFieldValues { get; } = new(StringComparer.Ordinal);

    // The pane each `GetProjectFieldValueAsync` call asked about, so a test can prove a contribution asks about its own session rather than whichever pane is selected.
    public List<string?> ProjectFieldPanesAsked { get; } = [];

    // The workflow triggers the plugin raised, in order — what picking an issue for a session starts.
    public List<(string TypeId, IReadOnlyDictionary<string, string> Data)> Triggers { get; } = [];

    public IPluginBackendChannel Channel { get; init; } = new InProcessChannel();

    public IServiceProvider Services => throw new NotSupportedException();

    public ICockpitActions Actions => throw new NotSupportedException();

    public IPluginStorage Storage { get; init; } = new InMemoryPluginStorage();

    public ICockpitSessionObserver Sessions => throw new NotSupportedException();

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

    public void RaiseWorkflowTrigger(string typeId, IReadOnlyDictionary<string, string> data) => Triggers.Add((typeId, data));

    public void AddSettings(Func<Control> createView) => throw new NotSupportedException();

    public void AddSideMenuButton(string title, Action onInvoke) => throw new NotSupportedException();

    public void AddSideMenuSection(string title, Func<Control> createView) => throw new NotSupportedException();

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        throw new NotSupportedException();

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        throw new NotSupportedException();

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
