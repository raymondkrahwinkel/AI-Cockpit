using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues.Contracts;

// AC-1396: what the backend part answers the UI part over the plugin's channel. Compiled into both assemblies as
// a linked source file rather than shared as an assembly, so the UI part never references the backend part.
internal static class GitHubIssuesChannel
{
    // Payload: GitHubIssuesSearchRequest. Answers a GitHubIssuesSearchResult — the open issues the settings' mode
    // finds: across the owner's repositories via gh, or the one repository over HTTP.
    public const string SearchIssues = "search-issues";

    // Payload: GitHubIssuesPickerRequest. Answers the open issues the session picker offers, via gh, with the
    // settings' picker terms, scoped to the repositories the pane's project is linked to when it has any.
    public const string PickerIssues = "picker-issues";

    // Payload: GitHubIssuesPaneRequest, pane unused. Answers the label names of the repositories the settings' mode covers.
    public const string ListLabels = "list-labels";

    // Payload: GitHubIssuesPaneRequest, pane unused. Answers the repositories the dialog's repository filter offers.
    public const string ListRepositories = "list-repositories";

    // Payload: GitHubIssuesPaneRequest. Answers the repository the pane's project is linked to, or null.
    public const string LinkedRepository = "linked-repository";

    // Payload: GitHubIssuesLinkRequest. Links the issue to the pane; answers null.
    public const string Link = "link";

    // Payload: GitHubIssuesPaneRequest. Unlinks whatever issue the pane had; answers null.
    public const string Unlink = "unlink";

    // Payload: GitHubIssuesPaneRequest. Answers the GitHubIssue linked to the pane, or null.
    public const string LinkedIssue = "linked-issue";

    // Payload: GitHubIssueRequest. Assigns the issue to the gh user; answers null.
    public const string AssignToMe = "assign-to-me";

    // Payload: GitHubIssueLabelRequest. Adds the label to the issue; answers null.
    public const string AddLabel = "add-label";

    // Payload: GitHubIssueCloseRequest. Closes the issue with the reason; answers null.
    public const string Close = "close";

    // Event, payload GitHubIssuesLinkChanged: a pane's linked issue changed, published by the backend part.
    public const string LinkChanged = "link-changed";

    // What a project's repository link is stored under (AC-317) — GitHubRepositoryField.Key, which the UI part
    // names too when it preselects a new session's project. Never change it: linked projects are keyed by it.
    public const string RepositoryFieldKey = "github.repository";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record GitHubIssuesSearchRequest(bool AssignedToMe, bool ForceRefresh, string? Label);

// PageLimit is the page size of the route the backend took, so the dialog can name it when the page was full.
internal sealed record GitHubIssuesSearchResult(IReadOnlyList<GitHubIssue> Issues, bool PossiblyTruncated, int PageLimit);

internal sealed record GitHubIssuesPickerRequest(string? PaneId, bool AssignedToMe);

internal sealed record GitHubIssuesPaneRequest(string? PaneId);

internal sealed record GitHubIssuesLinkRequest(string PaneId, GitHubIssue Issue, string? WorkingDirectory);

internal sealed record GitHubIssuesLinkChanged(string PaneId, GitHubIssue? Issue);

internal sealed record GitHubIssueRequest(string Repository, int Number);

internal sealed record GitHubIssueLabelRequest(string Repository, int Number, string Label);

internal sealed record GitHubIssueCloseRequest(string Repository, int Number, string Reason);
