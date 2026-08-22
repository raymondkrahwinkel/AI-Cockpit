using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the project-ownership claims plugins register (<c>ICockpitHost.ClaimProjectOwnership</c>, AC-604), so the
/// project editor resolves a project's badges without depending on the claiming plugin. Same shape as
/// <see cref="IProjectFieldRegistry"/>.
/// </summary>
public interface IProjectOwnershipRegistry
{
    /// <summary>Records a project's ownership claim. A project id that is already claimed is refused, first one wins.</summary>
    /// <returns>False when another plugin already claims this project — the caller says so; nothing throws.</returns>
    bool Register(ProjectOwnershipRegistration registration);

    /// <summary>
    /// Every <see cref="HostProjectField"/> the claiming registration resolves for <paramref name="projectId"/>,
    /// or null when nothing ever claimed this project (AC-604 acceptance criterion 4).
    /// </summary>
    IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>? Resolve(string projectId);
}

internal sealed class ProjectOwnershipRegistry : IProjectOwnershipRegistry, ISingletonService
{
    private static readonly HostProjectField[] _AllFields = Enum.GetValues<HostProjectField>();

    private readonly Dictionary<string, ProjectOwnershipRegistration> _claims = new(StringComparer.Ordinal);

    public bool Register(ProjectOwnershipRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.ProjectId) || _claims.ContainsKey(registration.ProjectId))
        {
            return false;
        }

        _claims.Add(registration.ProjectId, registration);
        return true;
    }

    public IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>? Resolve(string projectId)
    {
        if (!_claims.TryGetValue(projectId, out var registration))
        {
            return null;
        }

        return _AllFields.ToDictionary(
            field => field,
            field => registration.Overrides.TryGetValue(field, out var overrideValue) ? overrideValue : registration.Default);
    }
}
