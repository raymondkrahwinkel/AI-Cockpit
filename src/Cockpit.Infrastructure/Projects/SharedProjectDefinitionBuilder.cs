using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

/// <summary>Builds the portable definition used by sharing, saving and backfill.</summary>
public static class SharedProjectDefinitionBuilder
{
    /// <summary>Includes every resource for the source's write-time filter and reads an optional logo.</summary>
    public static async Task<SharedProjectPublishDefinition> BuildAsync(Project project)
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
