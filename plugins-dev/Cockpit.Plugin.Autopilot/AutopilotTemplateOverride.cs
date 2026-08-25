namespace Cockpit.Plugin.Autopilot;

// An operator's edit of a Plugin or Builtin template (AC-189). The original registration stays the source; an
// override just carries the operator's changed fields and wins over it when the combined list is built. "Reset
// to default" deletes the override, not the template.
internal sealed record AutopilotTemplateOverride(
    string Id,
    string Name,
    string Body,
    IReadOnlyList<string>? RequiredPlaceholders = null);
