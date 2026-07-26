using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the project fields plugins register (<c>ICockpitHost.AddProjectField</c>, AC-317), so the project editor can
/// draw them without depending on the plugins that contribute them. A registry of its own, the same shape as
/// <see cref="ITrackerProviderRegistry"/>. Empty until a plugin that links projects to something is installed.
/// </summary>
public interface IProjectFieldRegistry
{
    /// <summary>Records a project field. A key that is already registered is refused, first one wins.</summary>
    /// <returns>False when another plugin already contributes this key — the caller says so; nothing throws.</returns>
    bool Register(ProjectFieldRegistration registration);

    /// <summary>Every field registered so far, in registration order — the order the editor draws them in.</summary>
    IReadOnlyList<ProjectFieldRegistration> Fields { get; }
}

internal sealed class ProjectFieldRegistry : IProjectFieldRegistry, ISingletonService
{
    private readonly List<ProjectFieldRegistration> _fields = [];

    public IReadOnlyList<ProjectFieldRegistration> Fields => [.. _fields];

    // Keys match exactly, the way a project's stored links are looked up (Project.LinkedAs): a registry that
    // accepted "GitHub.Repository" as the same key would hand the editor a field whose saved value the plugin
    // then cannot find.
    public bool Register(ProjectFieldRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.Key)
            || _fields.Any(existing => string.Equals(existing.Key, registration.Key, StringComparison.Ordinal)))
        {
            return false;
        }

        _fields.Add(registration);
        return true;
    }
}
