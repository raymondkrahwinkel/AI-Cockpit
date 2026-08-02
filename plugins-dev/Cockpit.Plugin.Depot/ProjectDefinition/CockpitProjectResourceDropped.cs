namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>One resource row <see cref="CockpitProjectResourceFilter.Apply"/> left out of a written definition, and why (AC-244) — so a caller can tell the operator instead of the row just vanishing.</summary>
/// <param name="Portability">Null when <paramref name="Reference"/> is blank — that is not a path shape, so there is no portability to name.</param>
public sealed record CockpitProjectResourceDropped(string Role, string Reference, string? Label, ProjectResourcePortability? Portability);
