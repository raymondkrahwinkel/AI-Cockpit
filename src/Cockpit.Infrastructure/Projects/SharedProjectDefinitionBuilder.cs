using Cockpit.Core.Projects;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// Builds the portable definition used by sharing, saving and backfill.
public sealed class SharedProjectDefinitionBuilder : IProjectDefinitionBuilder<SharedProjectPublishDefinition>, ISingletonService
{
    public async Task<SharedProjectPublishDefinition> BuildAsync(Project project)
    {
        byte[]? logo = null;
        if (project.LogoPath is { Length: > 0 } path)
        {
            try
            {
                logo = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A missing decorative logo must not prevent publishing the project.
            }
        }

        return new SharedProjectPublishDefinition(
            project.Name, project.Description, project.GitUrl, project.BehaviorPrompt,
            project.IsolateInWorktreeByDefault, project.McpOverlay.EnabledServerNames,
            project.Resources.Select(resource => new SharedProjectPublishResource(
                resource.Role.ToString(), resource.Reference, resource.Label)).ToList(), logo);
    }
}
