using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// Lists open pull requests across all repositories for an owner via the local GitHub CLI (<c>gh search
/// prs --owner &lt;owner&gt; --state open --json …</c>), reusing the user's existing <c>gh</c> login — no
/// token to paste. Pull requests in archived repositories are excluded (resolved against <c>gh repo list
/// --archived</c>). Results are cached briefly per owner so reopening the dialog/section or clicking around
/// does not re-shell out on every view; the archived-repo list (which rarely changes) is cached longer.
/// Refresh forces a re-fetch.
/// </summary>
internal sealed class GitHubPrGhClient
{
    private static readonly TimeSpan PullRequestTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ArchivedTtl = TimeSpan.FromMinutes(10);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<GitHubPullRequest> PullRequests)> PullRequestCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (DateTimeOffset At, HashSet<string> Archived)> ArchivedCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<GitHubPullRequest>> SearchOpenPullRequestsAsync(string owner, bool assignedToMe, bool forceRefresh, CancellationToken cancellationToken)
    {
        var trimmedOwner = owner?.Trim();
        // "@me" (or blank) means the PRs I'm involved in across EVERY repo — not just the repos I own, which
        // is what --owner @me searches and which misses org repos I contribute to (e.g. a PR I opened on an
        // org repo). So for @me we search by --author @me (the PRs I opened) or --assignee @me (the
        // assigned-to-me filter), with no --owner; a concrete owner keeps the --owner-scoped browse.
        var isMe = string.IsNullOrWhiteSpace(trimmedOwner) || string.Equals(trimmedOwner, "@me", StringComparison.OrdinalIgnoreCase);
        var cacheKey = (isMe ? "@me" : trimmedOwner!) + (assignedToMe ? "|assignee" : string.Empty);

        if (!forceRefresh)
        {
            lock (CacheGate)
            {
                if (PullRequestCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.At < PullRequestTtl)
                {
                    return cached.PullRequests;
                }
            }
        }

        var searchArgs = new List<string>
        {
            "search", "prs", "--state", "open", "--limit", "100", "--json", "number,title,url,body,repository,author",
        };
        if (isMe)
        {
            // gh resolves @me to the authenticated user, so this stays login-free like the rest of the plugin.
            searchArgs.Add(assignedToMe ? "--assignee" : "--author");
            searchArgs.Add("@me");
        }
        else
        {
            searchArgs.Add("--owner");
            searchArgs.Add(trimmedOwner!);
            if (assignedToMe)
            {
                searchArgs.Add("--assignee");
                searchArgs.Add("@me");
            }
        }

        var pullRequests = _ParsePullRequests(await _RunGhAsync(searchArgs.ToArray(), cancellationToken));

        // The archived-repo exclusion only makes sense for a concrete owner's repos; an @me search spans many
        // owners, so it is left unfiltered (your own PRs show regardless of which repo they live in).
        var result = pullRequests;
        if (!isMe)
        {
            var archived = await _GetArchivedReposAsync(trimmedOwner!, forceRefresh, cancellationToken);
            if (archived.Count > 0)
            {
                result = pullRequests.Where(pullRequest => !archived.Contains(pullRequest.Repository)).ToList();
            }
        }

        lock (CacheGate)
        {
            PullRequestCache[cacheKey] = (DateTimeOffset.UtcNow, result);
        }

        return result;
    }

    // The archived repos for the owner; "@me"/blank means the current gh user (no owner argument). Cached
    // longer than pull requests since archiving is rare.
    private static async Task<HashSet<string>> _GetArchivedReposAsync(string owner, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh)
        {
            lock (CacheGate)
            {
                if (ArchivedCache.TryGetValue(owner, out var cached) && DateTimeOffset.UtcNow - cached.At < ArchivedTtl)
                {
                    return cached.Archived;
                }
            }
        }

        var args = new List<string> { "repo", "list" };
        if (!string.Equals(owner, "@me", StringComparison.OrdinalIgnoreCase))
        {
            args.Add(owner);
        }

        args.AddRange(["--archived", "--limit", "1000", "--json", "nameWithOwner"]);

        HashSet<string> result;
        try
        {
            var json = await _RunGhAsync(args.ToArray(), cancellationToken);
            using var document = JsonDocument.Parse(json);
            result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("nameWithOwner", out var nwo) && nwo.GetString() is { } name)
                {
                    result.Add(name);
                }
            }
        }
        catch
        {
            // If the archived list can't be fetched, fail open (show everything) rather than hiding pull requests.
            return [];
        }

        lock (CacheGate)
        {
            ArchivedCache[owner] = (DateTimeOffset.UtcNow, result);
        }

        return result;
    }

    private static async Task<string> _RunGhAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not run 'gh' — is the GitHub CLI installed and on PATH? ({exception.Message})", exception);
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"gh exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }

    private static IReadOnlyList<GitHubPullRequest> _ParsePullRequests(string json)
    {
        using var document = JsonDocument.Parse(json);
        var pullRequests = new List<GitHubPullRequest>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var number = element.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
            var title = element.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var url = element.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var body = element.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var repository = element.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object && repo.TryGetProperty("nameWithOwner", out var nwo)
                ? nwo.GetString() ?? string.Empty
                : string.Empty;
            var author = element.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var login)
                ? login.GetString() ?? string.Empty
                : string.Empty;
            pullRequests.Add(new GitHubPullRequest(number, title, url, body, repository, author));
        }

        return pullRequests;
    }
}
