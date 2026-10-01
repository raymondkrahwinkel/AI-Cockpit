using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewTests;

// AC-1375: the gateway moved to Infrastructure, and these tests still drive it over a real cockpit through the
// adapters production wires and, since AC-1435, the project catalog its Projects page reads. `sessions` must be the
// registry `cockpit` was built with, or the gateway sees no panes.
internal static class AssistantAgentGatewayGraph
{
    public static AssistantAgentGateway Over(
        CockpitViewModel cockpit,
        SessionRegistry sessions,
        ISessionProfileStore profiles,
        IAssistantSpawnAuditLog auditLog,
        IWorkspaceAgentGateway agents,
        IAgentMessageInbox inbox,
        IAgentNotifyAuditLog notifyAudit,
        IPluginProviderRegistry pluginProviders,
        SessionWatcher watcher,
        IAssistantSessionHost assistantSessionHost,
        IWorktreeManager? worktreeManager = null,
        ISharedProjectSourceRegistry? sharedProjectSources = null,
        IMcpServerCatalog? mcpServerCatalog = null,
        IProjectFieldRegistry? projectFields = null,
        ProjectCatalog? projects = null) =>
        new(
            sessions,
            DesktopLauncher.Over(cockpit, sessions, worktreeManager, mcpServerCatalog),
            projects ?? TestProjects.Catalog(sharedSources: sharedProjectSources),
            new ProjectComposer(profiles, mcpServerCatalog),
            watcher,
            new AssistantConversation(assistantSessionHost),
            new ExternalLinkOpener(),
            profiles,
            auditLog,
            agents,
            inbox,
            notifyAudit,
            pluginProviders,
            worktreeManager,
            projectFields);
}
