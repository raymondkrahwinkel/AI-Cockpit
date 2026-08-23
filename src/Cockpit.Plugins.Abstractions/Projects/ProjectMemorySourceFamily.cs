namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// A group of <see cref="ProjectMemorySourceRegistration"/>s a plugin offers as one entry in the project
/// editor's source picker (AC-499) — "Depot", say, covering however many connections the operator has
/// configured, rather than one dropdown row per connection.
/// </summary>
/// <remarks>
/// A registration opts into a family via <see cref="ProjectMemorySourceRegistration.FamilyKey"/>. Declaring a
/// family never requires a registration to exist for it yet — the picker can still offer "Depot" and lead the
/// operator to <see cref="ConfigureAsync"/>.
/// </remarks>
/// <param name="Key">
/// Groups <see cref="ProjectMemorySourceRegistration"/>s under this family — matched to
/// <see cref="ProjectMemorySourceRegistration.FamilyKey"/> case-insensitively, the same agreement
/// <see cref="ProjectMemorySourceRegistration.Scheme"/> makes for a stored reference. The first plugin to register a
/// key keeps it, the same rule <see cref="ProjectFieldRegistration.Key"/> follows for a project field.
/// </param>
/// <param name="Title">
/// What the picker's top-level dropdown shows for this family — "Depot".
/// </param>
public sealed record ProjectMemorySourceFamily(string Key, string Title)
{
    /// <summary>
    /// What the instance dropdown shows in place of a list when no registration currently carries this
    /// <see cref="Key"/> as its <see cref="ProjectMemorySourceRegistration.FamilyKey"/> — "No Depot server
    /// configured yet". Null falls back to a generic sentence naming no plugin, which is what a family that never
    /// set this looks like: the empty state still reads as an empty state, just without the family's own wording.
    /// </summary>
    public string? EmptyHint { get; init; }

    /// <summary>
    /// Opens wherever the operator configures an instance of this family — a plugin's own settings, most often
    /// <c>host.ShowSettingsAsync()</c>. The way out of the empty state <see cref="EmptyHint"/> names.
    /// </summary>
    /// <remarks>
    /// Null means no such place exists — the button that would call this is never shown at all.
    /// </remarks>
    public Func<CancellationToken, Task>? ConfigureAsync { get; init; }
}
