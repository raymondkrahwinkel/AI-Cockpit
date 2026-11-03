using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Tests.BackendApi;

internal static class InProcBackendContractTests
{
    internal sealed class MemoryDesks : ISessionDesks
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
