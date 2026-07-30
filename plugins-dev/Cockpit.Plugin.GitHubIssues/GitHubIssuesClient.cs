using System.Net.Http.Headers;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Fetches a single repository's open issues, and its labels, from the GitHub REST API over a plain
/// <see cref="HttpClient"/> (the HTTP mode, used when the GitHub CLI is off). The issues endpoint also returns
/// pull requests, which are filtered out. A token is optional — it lifts the rate limit and allows private
/// repositories.
/// </summary>
internal sealed class GitHubIssuesClient
{
    /// <summary>
    /// The REST page size for an issues fetch. <see cref="GetOpenIssuesAsync"/> compares the raw response array
    /// length against this — before pull requests are filtered out of it — so the dialog's "may be capped" warning
    /// (AC-519) is driven by a count taken where truncation actually happens, not by what is left after filtering.
    /// </summary>
    public const int IssuePageLimit = 100;

    /// <summary>Page size for a repo's label list — see <see cref="GitHubGhClient.LabelListLimit"/>, the gh-path equivalent.</summary>
    internal const int LabelListLimit = 100;

    /// <summary>The base address requests are sent to. Swappable so a test can point this at a local stub instead of the real GitHub API (AC-519). No production code path sets it; only the field's default is used outside tests.</summary>
    internal static string BaseUrl = "https://api.github.com";

    private static readonly HttpClient Http = new();

    /// <summary>
    /// Fetches the repository's open issues. The returned <c>WasTruncated</c> is measured against the raw response
    /// — the number of entries GitHub actually sent back, pull requests and all — before any of those are filtered
    /// out below (AC-519 fix: a page filled with pull requests must still warn, even though the issue list handed
    /// back is a great deal shorter than <see cref="IssuePageLimit"/>).
    /// </summary>
    public async Task<(IReadOnlyList<GitHubIssue> Issues, bool WasTruncated)> GetOpenIssuesAsync(string owner, string repo, string? token, bool assignedToMe, CancellationToken cancellationToken, string? label = null)
    {
        var repository = $"{owner}/{repo}";
        var query = $"state=open&per_page={IssuePageLimit}";
        if (assignedToMe)
        {
            // The REST issues endpoint filters by a username, not "@me", so resolve the token's own login
            // first. Without a token there is no "me" to resolve, so the filter is simply skipped (the CLI
            // mode is the login-free path for assigned-to-me).
            var login = string.IsNullOrWhiteSpace(token) ? null : await _ResolveLoginAsync(token, cancellationToken);
            if (!string.IsNullOrWhiteSpace(login))
            {
                query += $"&assignee={Uri.EscapeDataString(login)}";
            }
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            // Server-side, same as the gh path's "label:x" search term (AC-519) — filtering client-side over a
            // page that may already be capped would silently miss whatever was cut off.
            query += $"&labels={Uri.EscapeDataString(label)}";
        }

        var url = $"{BaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues?{query}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Cockpit-GitHubIssues-Plugin");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        // The raw count GitHub sent back for this page — measured here, before the pull-request filter below ever
        // runs, so it still reflects whether the server itself filled the page (AC-519 fix).
        var wasTruncated = document.RootElement.GetArrayLength() == IssuePageLimit;

        var issues = new List<GitHubIssue>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("pull_request", out _))
            {
                continue;
            }

            var number = element.GetProperty("number").GetInt32();
            var title = element.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var htmlUrl = element.TryGetProperty("html_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var body = element.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            issues.Add(new GitHubIssue(number, title, htmlUrl, body, repository) { Labels = GitHubIssueLabels.Read(element) });
        }

        return (issues, wasTruncated);
    }

    /// <summary>
    /// This one repository's labels (AC-519) — the REST equivalent of <see cref="GitHubGhClient.ListRepositoryLabelsAsync"/>,
    /// which HTTP mode has only the one repository to ask about.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRepositoryLabelsAsync(string owner, string repo, string? token, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/labels?per_page={LabelListLimit}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Cockpit-GitHubIssues-Plugin");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return GitHubIssueLabels.ReadListing(document.RootElement);
    }

    // The authenticated user's login for the assigned-to-me filter (the REST issues endpoint needs a username,
    // not "@me"). Returns null on any failure so the caller falls back to the unfiltered list rather than
    // erroring out the whole dialog.
    private static async Task<string?> _ResolveLoginAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/user");
            request.Headers.UserAgent.ParseAdd("Cockpit-GitHubIssues-Plugin");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
