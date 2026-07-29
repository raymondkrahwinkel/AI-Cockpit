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
/// <param name="ListLocationsAsync">
/// Carried straight from <see cref="ProjectMemorySourceRegistration.ListLocationsAsync"/> (AC-502) — null for
/// "Folder" and for a source that cannot enumerate its own locations, either of which leaves <c>Choose…</c> exactly
/// as disabled as it was before this member existed.
/// </param>
/// <param name="SignInAsync">Carried straight from <see cref="ProjectMemorySourceRegistration.SignInAsync"/>.</param>
public sealed record MemorySourceChoice(
    string Label,
    string? Scheme,
    Func<CancellationToken, Task<ProjectMemorySourceLocationsResult>>? ListLocationsAsync = null,
    Func<CancellationToken, Task<bool>>? SignInAsync = null);
