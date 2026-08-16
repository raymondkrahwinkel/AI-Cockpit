using System.Text.Json;

namespace Cockpit.Plugin.GitHubPullRequests;

// Turns one `statusCheckRollup` entry (AC-802) into a `PullRequestCheck` — shared by
// `SessionPullRequestStatusClient` and `GitHubPrGhClient.GetPullRequestStatusAsync` (AC-818), since both read
// the identical CheckRun / StatusContext shape.
internal static class PullRequestCheckRollupParser
{
    public static PullRequestCheck Parse(JsonElement element)
    {
        var name = element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? string.Empty
            : _String(element, "context");

        var typename = element.TryGetProperty("__typename", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var status = element.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var conclusion = element.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var state = element.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;

        TimeSpan? duration = null;
        if (element.TryGetProperty("startedAt", out var startedEl) && startedEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(startedEl.GetString(), out var started)
            && element.TryGetProperty("completedAt", out var completedEl) && completedEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(completedEl.GetString(), out var completed))
        {
            duration = completed - started;
        }

        return new PullRequestCheck(name, _DeriveState(typename, status, conclusion, state), duration);
    }

    // A CheckRun (a GitHub Actions job) reports status (QUEUED/IN_PROGRESS/COMPLETED) and, once completed, a
    // conclusion. A StatusContext (a legacy commit status) has no separate status: state alone carries both.
    private static PullRequestCheckState _DeriveState(string? typename, string? status, string? conclusion, string? state)
    {
        if (string.Equals(typename, "StatusContext", StringComparison.OrdinalIgnoreCase) || status is null)
        {
            return state?.ToUpperInvariant() switch
            {
                "SUCCESS" => PullRequestCheckState.Passed,
                "FAILURE" or "ERROR" => PullRequestCheckState.Failed,
                "PENDING" or "EXPECTED" => PullRequestCheckState.Running,
                _ => PullRequestCheckState.Other,
            };
        }

        if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return PullRequestCheckState.Running;
        }

        return conclusion?.ToUpperInvariant() switch
        {
            "SUCCESS" => PullRequestCheckState.Passed,
            "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" => PullRequestCheckState.Failed,
            _ => PullRequestCheckState.Other,
        };
    }

    private static string _String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
