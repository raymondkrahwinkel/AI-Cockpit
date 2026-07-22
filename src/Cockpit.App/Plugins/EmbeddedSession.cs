using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

/// <summary>The host's <see cref="IEmbeddedSession"/>: the session view to place, the pane id to act on it, the task that completes when the session ends, the callback that toggles its composer, and the host callback that ends this one session. The host owns the session, so there is nothing here to dispose.</summary>
internal sealed record EmbeddedSession(Control View, string PaneId, Task<string?> Completion, Action<bool> SetInput, Func<Task> Close) : IEmbeddedSession
{
    public void SetInputEnabled(bool enabled) => SetInput(enabled);

    public Task CloseAsync() => Close();
}
