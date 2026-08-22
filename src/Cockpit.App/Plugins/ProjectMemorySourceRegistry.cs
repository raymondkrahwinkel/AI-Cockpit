using Cockpit.Core.Abstractions;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the memory sources plugins register (<c>ICockpitHost.AddProjectMemorySource</c>, AC-165/166), so the
/// project editor's picker and a session's standing instructions can read them without depending on the plugin.
/// Same shape as <see cref="IProjectFieldRegistry"/>: first registration for a scheme wins, matched case-insensitively.
/// </summary>
public interface IProjectMemorySourceRegistry
{
    /// <summary>
    /// Records a memory source. Refused (returns false): an unusable <see cref="ProjectMemoryRef.IsUsableScheme"/>
    /// scheme, a blank title, a scheme already registered (first wins), or a blank instruction — the point of the seam.
    /// </summary>
    bool Register(ProjectMemorySourceRegistration registration);

    /// <summary>
    /// Withdraws the source under <paramref name="scheme"/> (AC-501), matched case-insensitively like
    /// <see cref="Register"/>'s collision check; no-op (returns false) when nothing is registered. Never rewrites
    /// a project's own stored <c>MemoryRef</c> — same restraint as <c>Project.PluginFields</c> (AC-166).
    /// </summary>
    bool Remove(string scheme);

    /// <summary>Every source registered so far, in registration order — the order the editor's picker offers them in.</summary>
    IReadOnlyList<ProjectMemorySourceRegistration> Sources { get; }

    /// <summary>
    /// Declares a family (AC-499) a source can opt into via <see cref="ProjectMemorySourceRegistration.FamilyKey"/>.
    /// Refused (false): a blank key/title, or a key already declared (first wins). No <c>RemoveFamily</c> yet —
    /// nothing un-declares a family once registered (AC-501); add one only once a caller needs it.
    /// </summary>
    bool RegisterFamily(ProjectMemorySourceFamily family);

    /// <summary>Every family declared so far, in declaration order — the order the editor's picker offers them in, ahead of any ungrouped source.</summary>
    IReadOnlyList<ProjectMemorySourceFamily> Families { get; }
}

internal sealed class ProjectMemorySourceRegistry : IProjectMemorySourceRegistry, ISingletonService
{
    private readonly List<ProjectMemorySourceRegistration> _sources = [];
    private readonly List<ProjectMemorySourceFamily> _families = [];

    public IReadOnlyList<ProjectMemorySourceRegistration> Sources => [.. _sources];

    public IReadOnlyList<ProjectMemorySourceFamily> Families => [.. _families];

    // Case-insensitive, unlike ProjectFieldRegistry.Register's key comparison: a project's MemoryRef is itself
    // matched case-insensitively (SessionStartDefaults), so a registry that told two plugins "depot" and "Depot"
    // apart would let one silently shadow the other's meaning for the very same stored reference.
    public bool Register(ProjectMemorySourceRegistration registration)
    {
        if (!ProjectMemoryRef.IsUsableScheme(registration.Scheme)
            || string.IsNullOrWhiteSpace(registration.Title)
            || string.IsNullOrWhiteSpace(registration.Instruction)
            || _sources.Any(existing => string.Equals(existing.Scheme, registration.Scheme, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _sources.Add(registration);
        return true;
    }

    public bool Remove(string scheme)
    {
        var index = _sources.FindIndex(existing => string.Equals(existing.Scheme, scheme, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        _sources.RemoveAt(index);
        return true;
    }

    // Case-insensitive, the same reason Register's own scheme comparison is: FamilyKey is matched against this key
    // case-insensitively too (ProjectMemorySourceRegistration.FamilyKey's own doc comment).
    public bool RegisterFamily(ProjectMemorySourceFamily family)
    {
        if (string.IsNullOrWhiteSpace(family.Key)
            || string.IsNullOrWhiteSpace(family.Title)
            || _families.Any(existing => string.Equals(existing.Key, family.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _families.Add(family);
        return true;
    }
}
