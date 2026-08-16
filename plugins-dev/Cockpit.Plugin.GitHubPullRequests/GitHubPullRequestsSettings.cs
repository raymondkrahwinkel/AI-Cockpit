using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

// The plugin's settings, persisted through the host's per-plugin `IPluginStorage`. Two modes:
// the local GitHub CLI (`UseGitHubCli` — uses your existing `gh` login and shows open
// pull requests across all repos for `GhOwner`), or a single repository over HTTP with an
// optional token. The prompt template dropped on click is editable either way.
internal sealed class GitHubPullRequestsSettings(IPluginStorage storage)
{
    public bool UseGitHubCli
    {
        get => storage.Get<bool>("useGhCli");
        set => storage.Set("useGhCli", value);
    }

    public string GhOwner
    {
        get => storage.Get<string>("ghOwner") is { Length: > 0 } owner ? owner : "@me";
        set => storage.Set("ghOwner", value);
    }

    public string Owner
    {
        get => storage.Get<string>("owner") ?? string.Empty;
        set => storage.Set("owner", value);
    }

    public string Repo
    {
        get => storage.Get<string>("repo") ?? string.Empty;
        set => storage.Set("repo", value);
    }

    public string Token
    {
        get => storage.Get<string>("token") ?? string.Empty;
        set => storage.Set("token", value);
    }

    public string Template
    {
        get => storage.Get<string>("template") ?? PromptTemplate.Default;
        set => storage.Set("template", value);
    }

    // Whether the get_pr_status MCP tool (AC-818) is offered to sessions. On by default until the operator turns it off.
    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }

    // Whether a pull request that starts waiting for your review raises a toast (default on). GitHub CLI mode only — the single-repo HTTP mode has no review-requested search.
    public bool NotifyOnReviewRequests
    {
        get => storage.Get<bool?>("notifyOnReviewRequests") ?? true;
        set => storage.Set("notifyOnReviewRequests", value);
    }

    // The review requests already announced, as `owner/repo#number`. `null` means "never looked" —
    // the first load primes this quietly instead of announcing every request that was already waiting.
    public IReadOnlySet<string>? SeenReviewRequests
    {
        get => storage.Get<string>("seenReviewRequests") is { } stored
            ? stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
            : null;
        set => storage.Set("seenReviewRequests", string.Join('\n', value ?? new HashSet<string>()));
    }

    // The pull requests the operator has set aside, by url: ones that are open for the long haul and live in a todo
    // somewhere, not in this list. Persisted, because a PR you ignored today is one you do not want to be looking at
    // tomorrow either — and kept as a list rather than dropped, so ignoring is a thing you can undo.
    public IReadOnlySet<string> IgnoredPullRequests
    {
        get => storage.Get<string>("ignoredPullRequests") is { } stored
            ? stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        set => storage.Set("ignoredPullRequests", string.Join('\n', value));
    }

    // Repositories set aside entirely: no pull request from them appears, whoever opened it.
    //
    // The per-pull-request ignore is for one thing you have decided about. This is for a repository that is
    // never your business — a fork, an archive-in-waiting, a dependency bot's playground — and ignoring its
    // pull requests one at a time is a chore that never ends, because it opens new ones.
    //
    // Kept, not deleted: the count offers them back, the same way a single ignored pull request comes back.
    public IReadOnlySet<string> IgnoredRepositories
    {
        get => storage.Get<string>("ignoredRepositories") is { } stored
            ? stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set => storage.Set("ignoredRepositories", string.Join('\n', value));
    }

    // Optional repository filter — one `owner/repo` per line (or comma-separated). When set, only pull
    // requests in those repositories are shown; blank means all repositories (the default).
    public string RepoFilter
    {
        get => storage.Get<string>("repoFilter") ?? string.Empty;
        set => storage.Set("repoFilter", value);
    }

    // The parsed `RepoFilter` as a set of `owner/repo` names, empty when no filter is set.
    public IReadOnlySet<string> RepoFilterSet =>
        RepoFilter
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Repositories or owners to watch, one per line (or comma-separated): `acme` for every repo of
    // that user/org, `acme/webshop` for the one.
    //
    // The rest of this list answers "which pull requests are mine" — authored by me, assigned to me, waiting on my
    // review. A repository you are responsible for asks a different question: what is open here, whoever opened it.
    // Five open pull requests in a project of yours, none of them yours, showed nothing at all.
    public string WatchedRepos
    {
        get => storage.Get<string>("watchedRepos") ?? string.Empty;
        set => storage.Set("watchedRepos", value);
    }

    // Watch every repository the operator is involved with — owned, collaborated on, or reached through an
    // organisation — whoever opened the pull request, with no list to keep up to date.
    //
    // Off by default: it is a wider net than "what is mine", and an operator with a hundred repositories should
    // choose that rather than discover it. Once on, `WatchedRepos` becomes unnecessary — it is for
    // watching something you are *not* involved with.
    public bool WatchEverythingIAmInvolvedWith
    {
        get => storage.Get<bool>("watchInvolved");
        set => storage.Set("watchInvolved", value);
    }

    // `WatchedRepos`, parsed.
    public IReadOnlyList<string> WatchedReposList =>
        [.. WatchedRepos
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}
