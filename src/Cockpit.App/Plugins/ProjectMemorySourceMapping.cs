using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Plugins;

/// <summary>
/// Maps a plugin's memory-source registration to the plain type <see cref="Cockpit.Core.Sessions.SessionStartDefaults"/>
/// reads (AC-165/166): Core does not reference the plugin contract, so this mapping happens here rather than there —
/// the same reason a project's own <see cref="ProjectMemorySource"/> exists as a separate, Core-only shape.
/// </summary>
public static class ProjectMemorySourceMapping
{
    public static IReadOnlyList<ProjectMemorySource> ToMemorySources(this IReadOnlyList<ProjectMemorySourceRegistration> registrations) =>
        [.. registrations.Select(registration => new ProjectMemorySource(registration.Scheme, registration.Title, registration.Instruction))];
}
