using System.Text.Json;

namespace Cockpit.Plugin.GitHubPullRequests.Contracts;

// AC-1396: what the backend part answers the UI part over the plugin's channel, and what it publishes to it.
// Compiled into both assemblies as a linked source file rather than shared as an assembly, so the UI part never
// references the backend part.
internal static class GitHubPullRequestsChannel
{
    // No payload. Answers a PullRequestFeedState: the refresh source's last known snapshot, never a wait.
    public const string Feed = "feed";

    // Payload: PullRequestRefreshRequest. Runs one refresh of the shared feed and answers a PullRequestRefreshAnswer;
    // the FeedUpdated event it raises reaches every subscriber, not only the one that asked.
    public const string Refresh = "refresh";

    // Payload: OpenPullRequestsRequest. Answers a PullRequestFeedResult with the dialog's own query, which the
    // "Assigned to me" toggle narrows server-side and so cannot be served from the shared feed.
    public const string OpenPullRequests = "open-pull-requests";

    // Payload: SessionPullRequestRequest. Answers the SessionPullRequestStatus of that checkout's branch, or null.
    public const string SessionPullRequest = "session-pull-request";

    // No payload. Answers a PullRequestBadgeState with the backend's current counts and no arrivals.
    public const string BadgeCounts = "badge-counts";

    // Event, payload PullRequestFeedState: the shared feed has a new snapshot, or a failed attempt to get one.
    public const string FeedUpdated = "feed-updated";

    // Event, payload PullRequestBadgeState: the counts behind the side-menu badge, plus the review requests that
    // arrived since the last look and should be announced.
    public const string BadgeChanged = "badge-changed";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The payload of an action that takes none: an empty object, so it survives a transport that serializes it.
    public static readonly JsonElement NoPayload = JsonSerializer.SerializeToElement(new { });
}

internal sealed record PullRequestRefreshRequest(bool ForceRefresh);

// `Ran` is false when another refresh was already in flight; `Error` is this call's own failure, only when it ran.
internal sealed record PullRequestRefreshAnswer(bool Ran, string? Error);

internal sealed record OpenPullRequestsRequest(bool AssignedToMe, bool ForceRefresh);

internal sealed record SessionPullRequestRequest(string WorkingDirectory);

// `LastError` is the message of the most recent failed attempt, null once one succeeds.
internal sealed record PullRequestFeedState(PullRequestFeedSnapshot Snapshot, string? LastError);

// Null counts mean "not yet known" (nothing fetched, or nothing configured to fetch), never a guessed zero.
internal sealed record PullRequestBadgeState(int? Mine, int? ReviewRequested, IReadOnlyList<GitHubPullRequest> Arrived)
{
    public static PullRequestBadgeState Unknown { get; } = new(null, null, []);
}
