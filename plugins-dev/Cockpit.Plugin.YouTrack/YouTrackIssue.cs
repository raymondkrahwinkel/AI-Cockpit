namespace Cockpit.Plugin.YouTrack;

/// <summary>One YouTrack issue shown in the side section, the dialog grid, and rendered into the prompt template. <see cref="State"/> is read from the issue's "State" or "Stage" custom field (project-specific; may be absent).</summary>
public sealed record YouTrackIssue(string Id, string IdReadable, string Summary, string? Description, string Project, string? State);
