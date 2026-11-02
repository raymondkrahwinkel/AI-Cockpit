using Cockpit.Core.Projects;

namespace Cockpit.Core.Abstractions.Projects;

/// <summary>Builds portable definitions without coupling Core to a source's definition contract.</summary>
public interface IProjectDefinitionBuilder<TDefinition>
{
    /// <summary>Builds the project's portable definition, including its optional logo.</summary>
    Task<TDefinition> BuildAsync(Project project);
}
