namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The two custom fields the workflow actions need from an issue: the one carrying its status, and the name of
/// the one carrying its assignee. Both are project-specific and may be absent — a project without a status field
/// simply offers no status actions, rather than the cockpit inventing one.
/// </summary>
internal sealed record YouTrackIssueFields(YouTrackStateField? State, string? AssigneeFieldName);
