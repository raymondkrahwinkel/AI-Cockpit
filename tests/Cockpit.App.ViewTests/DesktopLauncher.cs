using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1439: the backend's one launcher over a cockpit built by hand, through the seams production wires. `registry` must be
// the one `cockpit` was built with; the projects are the cockpit's own list, as the store behind it would hold them.
internal static class DesktopLauncher
{
    public static SessionLauncher Over(
        CockpitViewModel cockpit, SessionRegistry registry, IWorktreeManager? worktrees = null, IMcpServerCatalog? mcpServers = null)
    {
        var seams = new DesktopSessionSeams(() => cockpit);
        var catalog = mcpServers ?? Substitute.For<IMcpServerCatalog>();
        if (mcpServers is null)
        {
            catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<McpServerConfig>>([]));
        }

        return new SessionLauncher(
            seams, seams, registry, new SessionStartComposer(new CockpitProjects(cockpit), catalog, worktrees: worktrees),
            worktrees: worktrees, startObserver: seams);
    }

    private sealed class CockpitProjects(CockpitViewModel cockpit) : IProjectStore
    {
        public Task<ProjectSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            UiThreadCall.RunAsync(async () =>
            {
                if (cockpit.Projects.Projects.Count == 0)
                {
                    await cockpit.Projects.LoadAsync();
                }

                return new ProjectSettings { Projects = [.. cockpit.Projects.Projects] };
            });

        public Task SaveAsync(ProjectSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
