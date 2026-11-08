using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Infrastructure.Sessions;

public sealed class SessionRoleResolver(
    IProjectMemorySourceRegistry? memorySources = null,
    IAssistantProfileStore? assistantProfiles = null) : ISessionRoleCatalog, ISingletonService
{
    public async Task<RoleListResult> ListAsync(Project? project, SessionProfile? profile, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var brain = await ResolveAsync(project, profile, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            if (brain?.Source.ListRolesAsync is not { } list)
            {
                return new(RoleSourceOutcome.NoRoles, []);
            }
            var result = await list(brain.Value, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            return result with { Roles = result.Roles.Where(role => !string.Equals(role.Name, "none", StringComparison.OrdinalIgnoreCase)).ToArray() };
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(RoleSourceOutcome.Unavailable, []);
        }
    }

    public async Task<BrainSource?> ResolveAsync(Project? project, SessionProfile? profile, CancellationToken cancellationToken)
    {
        var sources = memorySources?.Sources ?? [];
        var chosen = SessionStartDefaults.ResolveAssistant(project, profile);
        if (!ProjectMemoryRef.IsBrainReference(chosen) && assistantProfiles is not null)
        {
            chosen = (await assistantProfiles.LoadAsync(cancellationToken).ConfigureAwait(false)).Profile?.Assistant;
        }

        if (ProjectMemoryRef.IsBrainReference(chosen))
        {
            ProjectMemoryRef.TryParse(chosen, out var scheme, out var value);
            var owner = sources.OrderByDescending(source => string.Equals(source.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(source => !string.IsNullOrWhiteSpace(source.BrainInstruction?.Invoke(value)));
            return owner is null ? null : new BrainSource(chosen ?? string.Empty, value, new[] { owner }.ToMemorySources()[0]);
        }

        var brains = new Dictionary<string, BrainSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (source.ListBrainsAsync is not { } list)
            {
                continue;
            }
            var result = await list(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Outcome != Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsOutcome.Success)
            {
                throw new InvalidOperationException("The brain list is unavailable.");
            }
            foreach (var brain in result.Locations)
            {
                brains.TryAdd(brain.Value, new BrainSource($"{source.Scheme}:{brain.Value}", brain.Value, new[] { source }.ToMemorySources()[0]));
            }
        }
        return brains.Count == 1 ? brains.Values.Single() : null;
    }

    public sealed record BrainSource(string Reference, string Value, ProjectMemorySource Source);
}
