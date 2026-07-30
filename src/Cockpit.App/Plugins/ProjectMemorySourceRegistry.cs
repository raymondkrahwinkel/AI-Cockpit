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

    /// <summary>
    /// Withdraws the source registered under <paramref name="scheme"/> (AC-501), matched the same case-insensitive
    /// way <see cref="Register"/> checks for a collision. A no-op, returning false, when nothing is registered under
    /// it. Removing a source never touches a project's own stored <c>MemoryRef</c> — the same restraint
    /// <c>Project.PluginFields</c> keeps when the plugin that once linked a project disappears (AC-166): a reference
    /// this leaves without a matching source just falls back to the unexplained-scheme sentence the next time a
    /// session reads it, rather than being rewritten out from under the project.
    /// </summary>
    /// <returns>True when a source was registered under this scheme and is now gone.</returns>
    bool Remove(string scheme);

    /// <summary>Every source registered so far, in registration order — the order the editor's picker offers them in.</summary>
    IReadOnlyList<ProjectMemorySourceRegistration> Sources { get; }

    /// <summary>
    /// Declares a family (AC-499) a source can later opt into via <see cref="ProjectMemorySourceRegistration.FamilyKey"/>.
    /// A blank <see cref="ProjectMemorySourceFamily.Key"/> or <see cref="ProjectMemorySourceFamily.Title"/> is
    /// refused for the same reason a blank scheme or title is refused by <see cref="Register"/> — nothing to key a
    /// registration on, or to label the picker's own entry with. A key already declared is refused too, first one
    /// wins, matched case-insensitively — the same agreement <see cref="Register"/>'s own scheme comparison makes.
    /// <para>
    /// No <c>RemoveFamily</c> counterpart to <see cref="Remove"/>: nothing in this codebase yet un-declares a
    /// family once its plugin has registered it (a Depot connection removed by AC-501's live-refresh removes that
    /// connection's own scheme, never the "Depot" family itself — the picker keeps offering it, empty-hint and all,
    /// which is the whole point of declaring a family separately from its instances). Add one only once a caller
    /// actually needs it.
    /// </para>
    /// </summary>
    /// <returns>False when the key or title is blank, or another plugin already declared this key.</returns>
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
