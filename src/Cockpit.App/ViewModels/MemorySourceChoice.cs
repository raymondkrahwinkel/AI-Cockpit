using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry in the project editor's memory-location picker (AC-165/166): "Folder" (<see cref="Scheme"/> null) or
/// one of the sources a plugin registered. Mirrors <see cref="TerminalShellChoice"/>'s shape for the same reason — a
/// combo box needs a label to show and a value to act on, and a record beats a bare string/null pair repeated at
/// every call site.
/// </summary>
/// <param name="Label">What the picker shows — "Folder", or the source's own <c>Title</c>.</param>
/// <param name="Scheme">The prefix this choice writes into <c>MemoryRef</c>, or null for "Folder".</param>
public sealed record MemorySourceChoice(string Label, string? Scheme)
{
    /// <summary>
    /// Carried straight from <see cref="ProjectMemorySourceRegistration.ListLocationsAsync"/> (AC-502) — null for
    /// "Folder" and for a source that cannot enumerate its own locations, either of which leaves <c>Choose…</c>
    /// exactly as disabled as it was before this member existed.
    /// </summary>
    public Func<CancellationToken, Task<ProjectMemorySourceLocationsResult>>? ListLocationsAsync { get; init; }

    /// <summary>Carried straight from <see cref="ProjectMemorySourceRegistration.SignInAsync"/> (AC-502).</summary>
    public Func<CancellationToken, Task<bool>>? SignInAsync { get; init; }

    /// <summary>
    /// The registered source's own reachability check (AC-503), carried alongside <see cref="Scheme"/> so a row's
    /// diagnostics can call it without reaching back into a registry this view model does not otherwise hold onto.
    /// Null for "Folder" and for a source whose plugin did not implement one — either way, nothing is shown under
    /// the row for it, the same as before AC-503 existed.
    /// </summary>
    public Func<string, CancellationToken, Task<ProjectMemorySourceReachabilityResult>>? CheckReachability { get; init; }
}
