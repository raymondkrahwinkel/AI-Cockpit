using System.Text.Json;

namespace Cockpit.Plugin.YouTrack.Contracts;

// AC-1397: what the UI part asks the backend part over the plugin's channel. Compiled into both assemblies as a
// linked source file rather than shared as an assembly, so the UI part never references the backend part. Every
// session-bound action names its pane explicitly: the backend never reads which session is "active".
internal static class YouTrackChannel
{
    // Payload: InstanceRequest. Answers the instance's projects (YouTrackProject[]), empty when it cannot say.
    public const string Projects = "projects";

    // Payload: PreferredTagsRequest. Answers the project tags a pane's cockpit project links to, else the default.
    public const string PreferredTags = "preferred-tags";

    // Payload: IssuesRequest. Answers the matching open issues (YouTrackIssue[]).
    public const string Issues = "issues";

    // Payload: ProjectStateFieldRequest. Answers a ProjectStateFieldAnswer.
    public const string ProjectStateField = "project-state-field";

    // Payload: IssueRequest. Answers the issue's YouTrackIssueFields.
    public const string IssueFields = "issue-fields";

    // Payload: SetStateRequest. Moves the issue; answers nothing. PaneId, when set, is the session it was moved for.
    public const string SetState = "set-state";

    // Payload: StartRequest. Moves the issue to its start target and assigns it; answers what happened as a string.
    public const string Start = "start";

    // Payload: LinkRequest. Ties an issue to one pane; answers nothing.
    public const string Link = "link";

    // Payload: PaneRequest. Unties a pane's issue; answers nothing.
    public const string Unlink = "unlink";

    // Payload: PaneRequest. Answers the pane's LinkedIssue, or null.
    public const string Linked = "linked";

    // Event, payload PaneRequest: a pane's linked issue changed.
    public const string LinkChanged = "link-changed";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record InstanceRequest(YouTrackInstance Instance);

internal sealed record PreferredTagsRequest(string? PaneId, string? DefaultProjectTag);

internal sealed record IssuesRequest(YouTrackInstance Instance, IReadOnlyList<string>? ProjectTags, string? Filter, bool AssignedToMe, int Top);

internal sealed record ProjectStateFieldRequest(YouTrackInstance Instance, string ProjectShortName);

internal sealed record ProjectStateFieldAnswer(string? FieldName, IReadOnlyList<string> Values);

internal sealed record IssueRequest(YouTrackInstance Instance, YouTrackIssue Issue);

internal sealed record SetStateRequest(YouTrackInstance Instance, YouTrackIssue Issue, YouTrackStateField State, string Target, string? PaneId);

internal sealed record StartRequest(YouTrackInstance Instance, YouTrackIssue Issue, YouTrackIssueFields Fields, string Target, string? PaneId);

internal sealed record LinkRequest(string PaneId, LinkedIssue Link);

internal sealed record PaneRequest(string PaneId);
