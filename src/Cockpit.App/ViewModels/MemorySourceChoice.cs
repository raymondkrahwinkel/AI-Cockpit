namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry in the project editor's memory-location picker (AC-165/166): "Folder" (<see cref="Scheme"/> null) or
/// one of the sources a plugin registered. Mirrors <see cref="TerminalShellChoice"/>'s shape for the same reason — a
/// combo box needs a label to show and a value to act on, and a record beats a bare string/null pair repeated at
/// every call site.
/// </summary>
/// <param name="Label">What the picker shows — "Folder", or the source's own <c>Title</c>.</param>
/// <param name="Scheme">The prefix this choice writes into <c>MemoryRef</c>, or null for "Folder".</param>
public sealed record MemorySourceChoice(string Label, string? Scheme);
