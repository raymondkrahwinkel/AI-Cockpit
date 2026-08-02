using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// One entry offered somewhere in the project editor's memory-location picker (AC-165/166, AC-499): "Folder"
// (`Scheme` and `FamilyKey` both null), one of the sources a plugin registered on its own
// (`Scheme` set, `FamilyKey` null — exactly today's shape), a family placeholder
// (`FamilyKey` set, `Scheme` null — the top-level "Depot" row a
// `ProjectResourceRowViewModel` shows its own instance dropdown under), or one instance within a
// family's own dropdown (`Scheme` set, same shape as an ungrouped source). Mirrors
// `TerminalShellChoice`'s shape for the same reason — a combo box needs a label to show and a value to
// act on, and a record beats a bare string/null pair repeated at every call site.
//
// One type doing both jobs rather than a second record for instances: an instance is, in every way that matters to
// a row, exactly the same shape an ungrouped source already was — a label, a scheme, and the three delegates below
// — so a family only changes which dropdown a source's entry appears in, never what the entry itself carries.
//
// `Label`: What the picker shows — "Folder", a family's own `Title`, or a source's `Title`/`InstanceTitle`.
// `Scheme`: The prefix this choice writes into `MemoryRef`, or null for "Folder" and for a family placeholder (its instance carries the scheme instead).
public sealed record MemorySourceChoice(string Label, string? Scheme)
{
    // Set only on a family placeholder entry (AC-499) — carried from `ProjectMemorySourceFamily.Key`,
    // null for "Folder" and for a leaf entry (an ungrouped source, or an instance inside a family's own dropdown).
    // What `ProjectResourceRowViewModel.ShowsMemorySourceServerRow` gates on: a row shows its instance
    // dropdown exactly when its top-level choice carries one of these.
    public string? FamilyKey { get; init; }

    // Set only on a family placeholder entry — carried from `ProjectMemorySourceFamily.EmptyHint`, shown
    // in place of the instance dropdown when the family has no registered instance yet.
    public string? EmptyHint { get; init; }

    // Set only on a family placeholder entry — carried from `ProjectMemorySourceFamily.ConfigureAsync`,
    // what the server row's "Servers…" button calls. Null means no button is shown at all (never a dead button).
    public Func<CancellationToken, Task>? ConfigureAsync { get; init; }

    // Carried straight from `ProjectMemorySourceRegistration.ListLocationsAsync` (AC-502) — null for
    // "Folder", a family placeholder, and a source that cannot enumerate its own locations, either of which leaves
    // `Choose…` exactly as disabled as it was before this member existed.
    public Func<CancellationToken, Task<ProjectMemorySourceLocationsResult>>? ListLocationsAsync { get; init; }

    // Carried straight from `ProjectMemorySourceRegistration.SignInAsync` (AC-502).
    public Func<CancellationToken, Task<bool>>? SignInAsync { get; init; }

    // The registered source's own reachability check (AC-503), carried alongside `Scheme` so a row's
    // diagnostics can call it without reaching back into a registry this view model does not otherwise hold onto.
    // Null for "Folder", a family placeholder, and a source whose plugin did not implement one — either way,
    // nothing is shown under the row for it, the same as before AC-503 existed.
    public Func<string, CancellationToken, Task<ProjectMemorySourceReachabilityResult>>? CheckReachability { get; init; }
}
