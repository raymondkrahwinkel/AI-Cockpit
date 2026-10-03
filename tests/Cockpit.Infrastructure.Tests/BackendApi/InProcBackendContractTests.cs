using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Events;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.BackendApi;

// AC-1441: the contract suite against the in-proc backend, the launcher, registry, hosting and event log the backend
// composes, with only the provider faked. The desk is held in memory: persisting it is no part of the contract.
public sealed class InProcBackendContractTests : BackendContractTests
{
    protected override async Task<ContractBackend> StartBackendAsync()
    {
        var registry = new SessionRegistry();
        var events = new BackendEventLog();
        var bridge = new SessionEventsBridge(registry, events);
        await bridge.StartAsync(CancellationToken.None);
        var launcher = new SessionLauncher(
            new MemoryDesks(),
            new BackendSessionHosting(new SessionControlFactory(new SessionManager(new ContractDrivers()))),
            registry,
            new SessionStartComposer(Substitute.For<IProjectStore>(), Substitute.For<IMcpServerCatalog>()));
        return new ContractBackend(launcher, registry, events, async () =>
        {
            foreach (var handle in registry.All.ToList())
            {
                await launcher.StopSessionAsync(handle.PaneId);
            }

            await bridge.StopAsync(CancellationToken.None);
        });
    }

    private sealed class ContractDrivers : ISessionDriverFactory
    {
        public ISessionDriver Create(SessionProfile? profile) => new ContractDriver();
    }

    private sealed class MemoryDesks : ISessionDesks
    {
        private readonly Lock _gate = new();

        public WorkspaceSettings Workspaces { get; private set; } = new();

        public Task<T> RunExclusiveAsync<T>(Func<T> decision)
        {
            lock (_gate)
            {
                return Task.FromResult(decision());
            }
        }

        public bool CanCloseWorkspace(string workspaceId) => true;

        public Task<Workspace> CreateSessionsWorkspaceAsync(string name)
        {
            var workspace = new Workspace(Guid.NewGuid().ToString("n"), name, WorkspaceType.Sessions);
            Workspaces = Workspaces with { Workspaces = [.. Workspaces.Workspaces, workspace] };
            return Task.FromResult(workspace);
        }

        public Task RenameWorkspaceAsync(string workspaceId, string name) => Task.CompletedTask;

        public Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId) => Task.FromResult(0);

        public Task AddPaneAsync(string workspaceId, WorkspacePane pane) => Task.CompletedTask;

        public Task RemovePaneAsync(string workspaceId, string paneId) => Task.CompletedTask;
    }
}
