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
        var result = await _WithoutArchivedAsync(pullRequests, forceRefresh, cancellationToken);

        lock (CacheGate)
        {
            PullRequestCache[cacheKey] = (DateTimeOffset.UtcNow, result);
        }

        return result;
    }

    /// <summary>
    /// The open pull requests that are waiting for <em>your</em> review (<c>--review-requested @me</c>) —
    /// across every repository, since a review request typically arrives on someone else's repo. Cached and
    /// force-refreshed on the same terms as the other searches.
    /// </summary>
    public async Task<IReadOnlyList<GitHubPullRequest>> SearchReviewRequestedAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        const string cacheKey = "@me|review-requested";

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

        var pullRequests = await _WithoutArchivedAsync(
            _ParsePullRequests(await _RunGhAsync(ReviewRequestedArguments, cancellationToken)),
            forceRefresh,
            cancellationToken);

        lock (CacheGate)
        {
            PullRequestCache[cacheKey] = (DateTimeOffset.UtcNow, pullRequests);
        }

        return pullRequests;
    }

    /// <summary>
    /// Every open pull request in a repository or owner the operator watches — <em>whoever</em> opened it.
    /// <para>
    /// The other searches all ask "which of these are mine": authored by me, assigned to me, waiting on my
    /// review. That is the right question for a personal list and the wrong one for a project you are
    /// responsible for: a repository with five open pull requests, none of them yours, shows nothing at all.
    /// A watched scope is <c>owner</c> (every repo of that user or org) or <c>owner/repo</c> (just the one).
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GitHubPullRequest>> SearchWatchedAsync(string scope, bool forceRefresh, CancellationToken cancellationToken)
    {
        var trimmed = scope.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var cacheKey = "watch|" + trimmed;
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

        // gh distinguishes the two: --repo takes owner/name, --owner takes the user or org and spans its repos.
        var scoped = trimmed.Contains('/') ? "--repo" : "--owner";
        var args = new[]
        {
            "search", "prs", "--state", "open", scoped, trimmed, "--limit", "100",
            "--json", "number,title,url,body,repository,author",
        };

        var pullRequests = await _WithoutArchivedAsync(
            _ParsePullRequests(await _RunGhAsync(args, cancellationToken)),
            forceRefresh,
            cancellationToken);

        lock (CacheGate)
        {
            PullRequestCache[cacheKey] = (DateTimeOffset.UtcNow, pullRequests);
        }

        return pullRequests;
    }

    /// <summary>
    /// Drops the pull requests that live in an archived repository.
    /// <para>
    /// The exclusion used to apply only when the operator had scoped the view to one owner, on the reasoning that
    /// an <c>@me</c> search spans owners and has no single repo list to check against. But a pull request in an
    /// archived repository cannot be merged, reviewed or closed — it is not open work, and a list of open work
    /// that contains it is wrong regardless of how the list was asked for. So the owners are taken from the
    /// results themselves (a repository is <c>owner/name</c>) and each one's archived repos are looked up — a
    /// handful of lookups, cached far longer than the pull requests, since archiving is rare.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<GitHubPullRequest>> _WithoutArchivedAsync(
        IReadOnlyList<GitHubPullRequest> pullRequests,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (pullRequests.Count == 0)
        {
            return pullRequests;
        }

        var owners = pullRequests
            .Select(pullRequest => pullRequest.Repository.Split('/', 2)[0])
            .Where(owner => owner.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in owners)
        {
            archived.UnionWith(await _GetArchivedReposAsync(owner, forceRefresh, cancellationToken));
        }

        return archived.Count == 0
            ? pullRequests
            : pullRequests.Where(pullRequest => !archived.Contains(pullRequest.Repository)).ToList();
    }

    /// <summary>
    /// The pull requests that have been merged recently — what the "merged" trigger watches (#69). Authored by the
    /// operator, because a flow that fired on every merge in every repository they can see would be a flow about other
    /// people's afternoons.
    /// </summary>
    public async Task<IReadOnlyList<GitHubPullRequest>> SearchMergedAsync(CancellationToken cancellationToken) =>
        _ParsePullRequests(await _RunGhAsync(MergedArguments, cancellationToken));

    // Kept as fields rather than inlined so the queries are assertable in a test without shelling out to gh.
    internal static readonly string[] MergedArguments =
    [
        "search", "prs", "--author", "@me", "--merged", "--limit", "30",
        "--json", "number,title,url,body,repository,author",
    ];

    internal static readonly string[] ReviewRequestedArguments =
    [
        "search", "prs", "--state", "open", "--review-requested", "@me", "--limit", "100",
        "--json", "number,title,url,body,repository,author",
    ];

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
