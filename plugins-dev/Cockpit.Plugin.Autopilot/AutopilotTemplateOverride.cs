namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// An operator's edit of a Plugin or Builtin template (AC-189). The original registration stays the source of the
/// template; an override just carries the operator's changed fields and wins over the registration when the combined
/// list is built. "Reset to default" is deleting the override, not the template — the registration then shows through
/// again unchanged. Keyed by the template's <see cref="AutopilotTemplate.Id"/>, and persisted (unlike the in-memory
/// registrations) so an edit survives a restart.
/// </summary>
/// <param name="Id">The id of the Plugin/Builtin template this overrides.</param>
/// <param name="Name">The operator's name for it.</param>
/// <param name="Body">The operator's brief text.</param>
/// <param name="RequiredPlaceholders">The operator's required-placeholder list, if they changed it. Optional.</param>
internal sealed record AutopilotTemplateOverride(
    string Id,
    string Name,
    string Body,
    IReadOnlyList<string>? RequiredPlaceholders = null);
