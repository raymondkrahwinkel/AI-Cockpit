using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Plugins;

// Maps a plugin's memory-source registration to the plain type `Cockpit.Core.Sessions.SessionStartDefaults`
// reads (AC-165/166): Core does not reference the plugin contract, so this mapping happens here rather than there —
// the same reason a project's own `ProjectMemorySource` exists as a separate, Core-only shape.
public static class ProjectMemorySourceMapping
{
    public static IReadOnlyList<ProjectMemorySource> ToMemorySources(this IReadOnlyList<ProjectMemorySourceRegistration> registrations) =>
        [.. registrations.Select(registration => new ProjectMemorySource(registration.Scheme, registration.Title, registration.Instruction)
        {
            BrainInstruction = registration.BrainInstruction,
            ListRolesAsync = registration.ListRolesAsync is not { } list ? null : async (brain, ct) =>
            {
                var result = await list(brain, ct).ConfigureAwait(false);
                Cockpit.Core.Projects.SessionRole[] roles = [.. result.Roles
                    .Where(role => !string.Equals(role.Name, "none", StringComparison.OrdinalIgnoreCase))
                    .Select(role => new Cockpit.Core.Projects.SessionRole(role.Name, role.Description))];
                return new Cockpit.Core.Projects.RoleListResult(
                    result.Outcome == Cockpit.Plugins.Abstractions.Projects.RoleSourceOutcome.Success && roles.Length == 0
                        ? Cockpit.Core.Projects.RoleSourceOutcome.NoRoles : _Outcome(result.Outcome), roles);
            },
            LoadRoleAsync = registration.LoadRoleAsync is not { } load ? null : async (brain, role, ct) =>
            {
                var result = await load(brain, role, ct).ConfigureAwait(false);
                return new Cockpit.Core.Projects.RoleLoadResult(_Outcome(result.Outcome), result.Instructions);
            },
            BrainMcpServerName = registration.BrainMcpServerName,
        })];

    private static Cockpit.Core.Projects.RoleSourceOutcome _Outcome(Cockpit.Plugins.Abstractions.Projects.RoleSourceOutcome outcome) => outcome switch
    {
        Cockpit.Plugins.Abstractions.Projects.RoleSourceOutcome.Success => Cockpit.Core.Projects.RoleSourceOutcome.Success,
        Cockpit.Plugins.Abstractions.Projects.RoleSourceOutcome.NoRoles => Cockpit.Core.Projects.RoleSourceOutcome.NoRoles,
        _ => Cockpit.Core.Projects.RoleSourceOutcome.Unavailable,
    };
}
