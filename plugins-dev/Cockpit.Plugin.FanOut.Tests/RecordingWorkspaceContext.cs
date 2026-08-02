using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.FanOut.Tests;

// A workspace context that keeps what the body asked it to start. The requests are the whole point: they are
// what the host would act on, so asserting on them is asserting on the run.
internal sealed class RecordingWorkspaceContext : IWorkspaceContext
{
    private readonly List<EmbeddedSessionRequest> _requests = [];

    public string WorkspaceId => "workspace-under-test";

    public IPluginStorage Storage { get; } = Substitute.For<IPluginStorage>();

    public ICockpitSessionObserver Sessions { get; } = Substitute.For<ICockpitSessionObserver>();

    public IReadOnlyList<EmbeddedSessionRequest> Requests => _requests;

    public IEmbeddedSession EmbedSession(EmbeddedSessionRequest request)
    {
        _requests.Add(request);
        return new FakeEmbeddedSession($"pane-{_requests.Count}");
    }

    public event EventHandler? RefreshRequested
    {
        add { }
        remove { }
    }
}
