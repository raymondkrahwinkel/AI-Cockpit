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
    private static readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<string> Repositories)> RepositoryCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<GitHubIssue>> SearchOpenIssuesAsync(string owner, bool assignedToMe, bool forceRefresh, CancellationToken cancellationToken, string? extraTerms = null)
    {
        var normalizedOwner = string.IsNullOrWhiteSpace(owner) ? "@me" : owner.Trim();
        // The assigned-to-me filter changes the server-side query, so it must key the cache separately —
        // otherwise toggling it would return the other set's cached results.
        var terms = extraTerms?.Trim() ?? string.Empty;

        // The extra terms change the server-side query, so they key the cache: two searches that ask different
        // questions must not answer each other's.
        var cacheKey = (assignedToMe ? normalizedOwner + "|@me" : normalizedOwner) + "|" + terms;

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
            "--limit", "100", "--json", "number,title,url,body,repository,labels",
        };
        if (assignedToMe)
        {
            // gh resolves @me to the authenticated user, so this stays login-free like the rest of the plugin.
            searchArgs.Add("--assignee");
            searchArgs.Add("@me");
        }

        // What the operator asked to narrow it by — GitHub's own search syntax, handed straight to gh: "-label:blocked",
        // "label:bug", "no:assignee". Each word is its own argument, because gh takes them that way.
        foreach (var term in terms.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            searchArgs.Add(term);
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

    /// <summary>
    /// The owner's repositories, as <c>owner/repo</c> — the source the project editor's repository field offers
    /// (AC-317). Deliberately <c>gh repo list</c> and not the repositories seen in the loaded issues: a repository with
    /// no open issue is still a repository this project can live in, and the issue-derived list has never been able to
    /// say so. Archived ones are left out for the same reason they are left out of the issue list. Cached as long as
    /// the archived list — a repository created a minute ago does not have to be in a dropdown a minute later.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListRepositoriesAsync(string owner, CancellationToken cancellationToken)
    {
        var normalizedOwner = string.IsNullOrWhiteSpace(owner) ? "@me" : owner.Trim();

        lock (CacheGate)
        {
            if (RepositoryCache.TryGetValue(normalizedOwner, out var cached) && DateTimeOffset.UtcNow - cached.At < ArchivedTtl)
            {
                return cached.Repositories;
            }
        }

        var args = new List<string> { "repo", "list" };
        if (!string.Equals(normalizedOwner, "@me", StringComparison.OrdinalIgnoreCase))
        {
            args.Add(normalizedOwner);
        }

        args.AddRange(["--no-archived", "--limit", "1000", "--json", "nameWithOwner"]);

        // Not caught: a failure here is the operator's to see. The archived list fails open because hiding issues is
        // worse than showing an archived one; an empty repository list is indistinguishable from "you have none".
        using var document = JsonDocument.Parse(await _RunGhAsync(args.ToArray(), cancellationToken));
        var repositories = document.RootElement.EnumerateArray()
            .Select(element => element.TryGetProperty("nameWithOwner", out var name) ? name.GetString() : null)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (CacheGate)
        {
            RepositoryCache[normalizedOwner] = (DateTimeOffset.UtcNow, repositories);
        }

        return repositories;
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
            issues.Add(new GitHubIssue(number, title, url, body, repository) { Labels = GitHubIssueLabels.Read(element) });
        }

        return issues;
    }
}
