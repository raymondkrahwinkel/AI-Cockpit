using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Lists open issues across all repositories for an owner via the local GitHub CLI (<c>gh search issues
/// --owner &lt;owner&gt; --state open --json …</c>), reusing the user's existing <c>gh</c> login — no token
/// to paste. Issues in archived repositories are excluded (resolved against <c>gh repo list --archived</c>).
/// Results are cached briefly per owner so reopening the dialog or clicking around does not re-shell out on
/// every view; the archived-repo list (which rarely changes) is cached longer. Refresh forces a re-fetch.
/// </summary>
internal sealed class GitHubGhClient
{
    private static readonly TimeSpan IssueTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ArchivedTtl = TimeSpan.FromMinutes(10);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<GitHubIssue> Issues)> IssueCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (DateTimeOffset At, HashSet<string> Archived)> ArchivedCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<GitHubIssue>> SearchOpenIssuesAsync(string owner, bool assignedToMe, bool forceRefresh, CancellationToken cancellationToken)
    {
        var normalizedOwner = string.IsNullOrWhiteSpace(owner) ? "@me" : owner.Trim();
        // The assigned-to-me filter changes the server-side query, so it must key the cache separately —
        // otherwise toggling it would return the other set's cached results.
        var cacheKey = assignedToMe ? normalizedOwner + "|@me" : normalizedOwner;

        if (!forceRefresh)
        {
            lock (CacheGate)
            {
                if (IssueCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.At < IssueTtl)
                {
                    return cached.Issues;
                }
            }
        }

        var archived = await _GetArchivedReposAsync(normalizedOwner, forceRefresh, cancellationToken);

        var searchArgs = new List<string>
        {
            "search", "issues", "--owner", normalizedOwner, "--state", "open",
            "--limit", "100", "--json", "number,title,url,body,repository",
        };
        if (assignedToMe)
        {
            // gh resolves @me to the authenticated user, so this stays login-free like the rest of the plugin.
            searchArgs.Add("--assignee");
            searchArgs.Add("@me");
        }

        var issues = _ParseIssues(await _RunGhAsync(searchArgs.ToArray(), cancellationToken));
        var result = archived.Count == 0
            ? issues
            : issues.Where(issue => !archived.Contains(issue.Repository)).ToList();

        lock (CacheGate)
        {
            IssueCache[cacheKey] = (DateTimeOffset.UtcNow, result);
        }

        return result;
    }

    // The archived repos for the owner; "@me"/blank means the current gh user (no owner argument). Cached
    // longer than issues since archiving is rare.
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
            // If the archived list can't be fetched, fail open (show everything) rather than hiding issues.
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

    private static IReadOnlyList<GitHubIssue> _ParseIssues(string json)
    {
        using var document = JsonDocument.Parse(json);
        var issues = new List<GitHubIssue>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var number = element.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
            var title = element.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var url = element.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var body = element.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var repository = element.TryGetProperty("repository", out var repo) && repo.TryGetProperty("nameWithOwner", out var nwo)
                ? nwo.GetString() ?? string.Empty
                : string.Empty;
            issues.Add(new GitHubIssue(number, title, url, body, repository));
        }

        return issues;
    }
}
