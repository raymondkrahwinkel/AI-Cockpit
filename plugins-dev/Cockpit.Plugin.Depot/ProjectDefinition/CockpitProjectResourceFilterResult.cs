namespace Cockpit.Plugin.Depot.ProjectDefinition;

// What `CockpitProjectResourceFilter.Apply` made of a candidate resource list (AC-244): what stays portable, and what was left out and why.
public sealed record CockpitProjectResourceFilterResult(
    IReadOnlyList<CockpitProjectResourceEntry> Portable, IReadOnlyList<CockpitProjectResourceDropped> Dropped);
