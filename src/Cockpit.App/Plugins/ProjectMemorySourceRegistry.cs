using Cockpit.Core.Abstractions;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the memory sources plugins register (<c>ICockpitHost.AddProjectMemorySource</c>, AC-165/166), so the
/// project editor's picker and a starting session's standing instructions can both read them without depending on
/// the plugin that contributes one. A registry of its own, the same shape as <see cref="IProjectFieldRegistry"/>:
/// first registration for a scheme wins, matched case-insensitively because a project's own stored reference is
/// matched the same way when it is read back (<see cref="Cockpit.Core.Sessions.SessionStartDefaults"/>). Empty
/// until a plugin that is not a folder-of-notes is installed.
/// </summary>
public interface IProjectMemorySourceRegistry
{
    /// <summary>
    /// Records a memory source. A scheme <see cref="ProjectMemoryRef.IsUsableScheme"/> refuses — blank, a single
    /// character, containing a colon, or wrapped in whitespace — is refused, because any of those is a scheme the
    /// parser that reads a stored reference back could never match to this registration: the picker would offer a
    /// source that then falls silent. A blank title is refused too — it gives the picker nothing to label its entry
    /// with — and a scheme already registered is refused as well, first one wins.
    /// <para>
    /// A blank instruction is refused for a different reason than the other two: it is not a cosmetic gap but the
    /// whole point of the seam. Naming a place a session cannot be told how to reach leaves it no better off than
    /// the bare reference it would otherwise have been handed, so such a source is not offered at all rather than
    /// offered half-working.
    /// </para>
    /// </summary>
    /// <returns>
    /// False when the scheme is not one <see cref="ProjectMemoryRef.IsUsableScheme"/> accepts, the title or
    /// instruction is blank, or another plugin already contributes this scheme.
    /// </returns>
    bool Register(ProjectMemorySourceRegistration registration);

    /// <summary>Every source registered so far, in registration order — the order the editor's picker offers them in.</summary>
    IReadOnlyList<ProjectMemorySourceRegistration> Sources { get; }
}

internal sealed class ProjectMemorySourceRegistry : IProjectMemorySourceRegistry, ISingletonService
{
    private readonly List<ProjectMemorySourceRegistration> _sources = [];

    public IReadOnlyList<ProjectMemorySourceRegistration> Sources => [.. _sources];

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
}
