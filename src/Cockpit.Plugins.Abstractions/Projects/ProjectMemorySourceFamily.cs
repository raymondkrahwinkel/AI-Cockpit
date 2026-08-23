namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// A group of <see cref="ProjectMemorySourceRegistration"/>s a plugin offers as one entry in the project editor's
/// source picker (AC-499) — "Depot", say, covering however many Depot connections the operator has configured,
/// rather than one dropdown row per connection. Registering a family is what turns the doorless-Depot dead end into
/// a way out: a plugin whose scheme space is empty right now (no connections configured yet) still declares its
/// family, so the picker can offer "Depot" and lead the operator to <see cref="ConfigureAsync"/> instead of leaving
/// the picker unable to say Depot exists at all.
/// <para>
/// A registration opts into a family via <see cref="ProjectMemorySourceRegistration.FamilyKey"/>; a family with no
/// matching registration yet is exactly the "no instances configured" state <see cref="EmptyHint"/> exists for.
/// Declaring a family never requires a registration to exist for it, and removing every registration under a key
/// never removes the family itself — the picker keeps offering "Depot" so the empty state stays reachable, not gone.
/// </para>
/// </summary>
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
    /// Opens wherever the operator configures an instance of this family — a plugin's own settings, reached the
    /// same way a "Configure…" button elsewhere in the app would (<c>host.ShowSettingsAsync()</c>, most often).
    /// This is the way out of the empty state <see cref="EmptyHint"/> names: the picker's own "Servers…" button
    /// calls this rather than sending the operator hunting for a settings screen the dialog never named.
    /// <para>
    /// Null means no such place exists (or none was wired up yet) — the button that would call this is never shown
    /// at all, the same "never a dead button" rule <see cref="ProjectMemorySourceRegistration.ListLocationsAsync"/>
    /// already follows for <c>Choose…</c>.
    /// </para>
    /// </summary>
    public Func<CancellationToken, Task>? ConfigureAsync { get; init; }
}
