namespace Cockpit.Plugin.Depot.ProjectDefinition;

// What CockpitProjectSensitiveFieldFilter.Apply made of a project's IsSecret AdditionalInfo rows (AC-607):
// what travels encrypted, and what was left out and why.
public sealed record CockpitProjectSensitiveFieldFilterResult(
    IReadOnlyList<CockpitProjectSensitiveFieldEntry> Encrypted, IReadOnlyList<CockpitProjectSensitiveFieldDropped> Dropped);
