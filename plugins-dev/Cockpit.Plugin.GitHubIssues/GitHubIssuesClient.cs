using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Fetches a repository's open issues from the GitHub REST API over a plain <see cref="HttpClient"/> (no
/// third-party dependency, so the plugin folder ships only its own assembly). The issues endpoint also
/// returns pull requests, which are filtered out. A token is optional — it lifts the rate limit and allows
/// private repositories.
/// </summary>
internal sealed class GitHubIssuesClient
{
    private static readonly HttpClient Http = new();

    public async Task<IReadOnlyList<GitHubIssue>> GetOpenIssuesAsync(string owner, string repo, string? token, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues?state=open&per_page=30";
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
            // The issues endpoint returns PRs too; a pull_request member marks those — skip them.
            if (element.TryGetProperty("pull_request", out _))
            {
                continue;
            }

            var number = element.GetProperty("number").GetInt32();
            var title = element.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var htmlUrl = element.TryGetProperty("html_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var body = element.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            issues.Add(new GitHubIssue(number, title, htmlUrl, body));
        }

        return issues;
    }
}
