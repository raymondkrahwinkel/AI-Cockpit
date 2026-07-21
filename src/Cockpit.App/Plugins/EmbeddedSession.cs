using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

/// <summary>The host's <see cref="IEmbeddedSession"/>: the session view to place, the pane id to act on it, and the host callback that ends this one session. The host owns the session, so there is nothing here to dispose.</summary>
internal sealed record EmbeddedSession(Control View, string PaneId, Func<Task> Close) : IEmbeddedSession
{
    public Task CloseAsync() => Close();
}
