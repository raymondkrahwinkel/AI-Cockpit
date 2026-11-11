using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Configuration;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.HeavyRuns;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Sessions;

// AC-1439: the backend's own host for a launched session: an SDK host behind a `SessionHostHandle`.
// AC-2288: a TTY session's pty is held here too, for a remote window to read and type through.
public sealed class BackendSessionHosting(
    SessionControlFactory controls,
    HeavyRunQueue? heavyRuns = null,
    ITtyLauncher? ttyLauncher = null,
    ITtySessionProviderResolver? ttyProviders = null,
    ISessionResourceResolver? sessionResources = null) : ISessionHosting
{
    public bool CanHost(PaneSessionKind kind) =>
        kind == PaneSessionKind.Sdk || (kind == PaneSessionKind.Tty && ttyLauncher is not null && ttyProviders is not null);

    public bool HostsTtyByDefault => false;

    public IHostedSession? Host(SessionHostingRequest request)
    {
        if (!CanHost(request.Kind))
        {
            return null;
        }

        if (request.Kind == PaneSessionKind.Tty)
        {
            return new BackendTtySession(request, ttyLauncher!, ttyProviders!, sessionResources);
        }

        var paneId = request.PaneId;
        var handle = new SessionHostHandle(
            paneId, request.Name, request.NameIsChosen, request.Request.WorkspaceId, request.WorkingDirectory,
            request.Request.Profile.Label, controls.CreateHost(() => paneId), request.Request.ProjectId, heavyRuns)
        {
            StartedByTheAssistant = request.Request.StartedByTheAssistant,
        };
        _ = handle.SetWorktreeBranchAsync(request.WorktreeBranch);
        return handle;
    }

    public Task StopAsync(IHostedSession session) => session switch
    {
        SessionHostHandle handle => handle.DisposeAsync().AsTask(),
        BackendTtySession tty => tty.DisposeAsync().AsTask(),
        _ => Task.CompletedTask,
    };
}

// AC-1379: the assistant on no desk, as the registry's `Assistant`, never in `All`. Its host starts it and holds the only
// reference; nothing here stops it, since only that host ever ends it.
public sealed class BackendAssistantSessions(
    SessionRegistry registry,
    ISessionDesks desks,
    SessionControlFactory controls,
    HeavyRunQueue? heavyRuns = null) : IAssistantSessionFactory
{
    public IAssistantSession? CreateAssistantSession()
    {
        var handle = new SessionHostHandle(
            AssistantIdentity.PaneId, AssistantProfileSlot.DisplayName, nameIsChosen: true,
            desks.Workspaces.Workspaces.FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions)?.Id ?? string.Empty,
            CockpitBuild.StateRoot, AssistantProfileSlot.DisplayName, controls.CreateHost(() => AssistantIdentity.PaneId),
            heavyRuns: heavyRuns);
        registry.RegisterAssistant(handle);
        return handle;
    }

    // Forgotten only while it is still the one held, as `CockpitViewModel.ReleaseAssistantSession` does on the desktop.
    public void ReleaseAssistantSession(IAssistantSession session)
    {
        if (ReferenceEquals(registry.Assistant, session))
        {
            registry.UnregisterAssistant();
        }
    }
}
