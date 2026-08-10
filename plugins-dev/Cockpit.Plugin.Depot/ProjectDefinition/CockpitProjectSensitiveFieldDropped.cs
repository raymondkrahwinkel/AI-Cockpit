namespace Cockpit.Plugin.Depot.ProjectDefinition;

// One sensitive AdditionalInfo row CockpitProjectSensitiveFieldFilter.Apply left out of the written definition,
// and why (AC-607) — mirrors CockpitProjectResourceDropped's reporting idiom (AC-244).
public sealed record CockpitProjectSensitiveFieldDropped(string Label, string Reason);
