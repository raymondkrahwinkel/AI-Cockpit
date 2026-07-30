using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry offered somewhere in the project editor's memory-location picker (AC-165/166, AC-499): "Folder"
/// (<see cref="Scheme"/> and <see cref="FamilyKey"/> both null), one of the sources a plugin registered on its own
/// (<see cref="Scheme"/> set, <see cref="FamilyKey"/> null — exactly today's shape), a family placeholder
/// (<see cref="FamilyKey"/> set, <see cref="Scheme"/> null — the top-level "Depot" row a
/// <see cref="ProjectResourceRowViewModel"/> shows its own instance dropdown under), or one instance within a
/// family's own dropdown (<see cref="Scheme"/> set, same shape as an ungrouped source). Mirrors
/// <see cref="TerminalShellChoice"/>'s shape for the same reason — a combo box needs a label to show and a value to
/// act on, and a record beats a bare string/null pair repeated at every call site.
/// <para>
/// One type doing both jobs rather than a second record for instances: an instance is, in every way that matters to
/// a row, exactly the same shape an ungrouped source already was — a label, a scheme, and the three delegates below
/// — so a family only changes which dropdown a source's entry appears in, never what the entry itself carries.
/// </para>
/// </summary>
/// <param name="Label">What the picker shows — "Folder", a family's own <c>Title</c>, or a source's <c>Title</c>/<c>InstanceTitle</c>.</param>
/// <param name="Scheme">The prefix this choice writes into <c>MemoryRef</c>, or null for "Folder" and for a family placeholder (its instance carries the scheme instead).</param>
public sealed record MemorySourceChoice(string Label, string? Scheme)
{
    /// <summary>
    /// Set only on a family placeholder entry (AC-499) — carried from <see cref="ProjectMemorySourceFamily.Key"/>,
    /// null for "Folder" and for a leaf entry (an ungrouped source, or an instance inside a family's own dropdown).
    /// What <see cref="ProjectResourceRowViewModel.ShowsMemorySourceServerRow"/> gates on: a row shows its instance
    /// dropdown exactly when its top-level choice carries one of these.
    /// </summary>
    public string? FamilyKey { get; init; }

    /// <summary>
    /// Set only on a family placeholder entry — carried from <see cref="ProjectMemorySourceFamily.EmptyHint"/>, shown
    /// in place of the instance dropdown when the family has no registered instance yet.
    /// </summary>
    public string? EmptyHint { get; init; }

    /// <summary>
    /// Set only on a family placeholder entry — carried from <see cref="ProjectMemorySourceFamily.ConfigureAsync"/>,
    /// what the server row's "Servers…" button calls. Null means no button is shown at all (never a dead button).
    /// </summary>
    public Func<CancellationToken, Task>? ConfigureAsync { get; init; }

    /// <summary>
    /// Carried straight from <see cref="ProjectMemorySourceRegistration.ListLocationsAsync"/> (AC-502) — null for
    /// "Folder", a family placeholder, and a source that cannot enumerate its own locations, either of which leaves
    /// <c>Choose…</c> exactly as disabled as it was before this member existed.
    /// </summary>
    public Func<CancellationToken, Task<ProjectMemorySourceLocationsResult>>? ListLocationsAsync { get; init; }

    /// <summary>Carried straight from <see cref="ProjectMemorySourceRegistration.SignInAsync"/> (AC-502).</summary>
    public Func<CancellationToken, Task<bool>>? SignInAsync { get; init; }

    /// <summary>
    /// The registered source's own reachability check (AC-503), carried alongside <see cref="Scheme"/> so a row's
    /// diagnostics can call it without reaching back into a registry this view model does not otherwise hold onto.
    /// Null for "Folder", a family placeholder, and a source whose plugin did not implement one — either way,
    /// nothing is shown under the row for it, the same as before AC-503 existed.
    /// </summary>
    public Func<string, CancellationToken, Task<ProjectMemorySourceReachabilityResult>>? CheckReachability { get; init; }
}
