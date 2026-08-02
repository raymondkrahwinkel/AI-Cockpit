using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.FanOut.Tests;

// A stand-in for a host-owned embedded session: a placeable view and nothing running behind it.
internal sealed class FakeEmbeddedSession : IEmbeddedSession
{
    public FakeEmbeddedSession(string paneId) => PaneId = paneId;

    public Control View { get; } = new Border();

    public string PaneId { get; }

    public Task CloseAsync() => Task.CompletedTask;

    public void SetInputEnabled(bool enabled)
    {
    }

    public Task<string?> Completion { get; } = Task.FromResult<string?>(null);
}
