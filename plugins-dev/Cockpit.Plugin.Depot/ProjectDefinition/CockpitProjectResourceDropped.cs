namespace Cockpit.Plugin.Depot.ProjectDefinition;

// One resource row `CockpitProjectResourceFilter.Apply` left out of a written definition, and why (AC-244) — so a caller can tell the operator instead of the row just vanishing.
//
// `Portability`: Null when `Reference` is blank — that is not a path shape, so there is no portability to name.
public sealed record CockpitProjectResourceDropped(string Role, string Reference, string? Label, ProjectResourcePortability? Portability);
