using System.Net.Http.Headers;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Fetches a single repository's open issues from the GitHub REST API over a plain <see cref="HttpClient"/>
/// (the HTTP mode, used when the GitHub CLI is off). The issues endpoint also returns pull requests, which
/// are filtered out. A token is optional — it lifts the rate limit and allows private repositories.
/// </summary>
internal sealed class GitHubIssuesClient
{
    private static readonly HttpClient Http = new();

    public async Task<IReadOnlyList<GitHubIssue>> GetOpenIssuesAsync(string owner, string repo, string? token, bool assignedToMe, CancellationToken cancellationToken)
    {
        var repository = $"{owner}/{repo}";
        var query = "state=open&per_page=100";
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

        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues?{query}";
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
            issues.Add(new GitHubIssue(number, title, htmlUrl, body, repository));
        }

        return issues;
    }

    // The authenticated user's login for the assigned-to-me filter (the REST issues endpoint needs a username,
    // not "@me"). Returns null on any failure so the caller falls back to the unfiltered list rather than
    // erroring out the whole dialog.
    private static async Task<string?> _ResolveLoginAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
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
