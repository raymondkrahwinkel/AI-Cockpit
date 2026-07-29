namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// One location a memory source's <c>Choose…</c> picker can offer (AC-502) — a Depot project, say. Shown by
/// <see cref="Name"/>, not <see cref="Value"/>: the picker exists precisely so the operator chooses a name instead
/// of typing a slug that is not shown anywhere else.
/// </summary>
/// <param name="Value">
/// The bare value written into the reference box when this location is picked — the same value a dev who knows it
/// would type by hand, folded into <c>&lt;scheme&gt;:&lt;value&gt;</c> the same way the dropdown above already does.
/// </param>
/// <param name="Name">What the picker shows for this location — a project's display name, not its slug.</param>
/// <param name="Detail">
/// An optional second line under <see cref="Name"/> — document count, last-modified, role — or <see langword="null"/>
/// when the source has nothing more to say about a location than its name.
/// </param>
public sealed record ProjectMemorySourceLocation(string Value, string Name, string? Detail = null);
