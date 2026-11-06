using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Sessions;

internal sealed class SessionMcpRestore(SessionMcpMounts mounts, IMcpServerCatalog catalog, SessionMcpGatewayHost? gateway = null, ISessionRegistry? sessions = null) : ISessionMcpRestore, ISingletonService
{
    public bool IsEnabled => gateway?.IsEnabled == true && gateway.BoundUrl is not null;

    public async Task<IReadOnlySet<string>?> RestoreAsync(SessionRestorePlan plan, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || plan.Profile is not { } profile)
        {
            return null;
        }
        var state = plan.State;
        var delegated = state?.McpIsDelegated == true || state?.McpDelegationCeiling is not null
            || profile.DelegationPolicy.AllowedAsTarget;
        if (state?.McpServerNames is null && !delegated)
        {
            return null;
        }
        var names = new ExactMcpServerSelection(state?.McpServerNames ?? profile.EnabledMcpServerNames ?? [])
        {
            AssistantManaged = state?.McpManager == SessionMcpManager.Assistant && sessions?.Find(plan.Pane.Id)?.StartedByTheAssistant == true,
        };
        var registry = await catalog.GetServersForSessionAsync(plan.Pane.ProjectId, names, cancellationToken);
        if (state?.McpServerNames is null && profile.EnabledMcpServerNames is null)
        {
            names.UnionWith(registry.Where(server => server.Enabled && !server.Internal).Select(server => server.Name));
        }
        var profileCeiling = DelegationService._NarrowServersFor(registry, profile, null);
        var ceiling = state?.McpDelegationCeiling?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? (delegated ? profileCeiling.ToHashSet(StringComparer.OrdinalIgnoreCase) : null);
        if (ceiling is not null)
        {
            ceiling.IntersectWith(profileCeiling);
            mounts.RestrictDelegation(plan.Pane.Id, ceiling);
            names.IntersectWith(ceiling.Concat(registry.Where(server => server.AlwaysMounted).Select(server => server.Name)));
        }
        names.UnionWith(registry.Where(server => server.AlwaysMounted).Select(server => server.Name));
        mounts.Grant(plan.Pane.Id, names.ToArray(), preserveSelection: true);
        return names;
    }
}
