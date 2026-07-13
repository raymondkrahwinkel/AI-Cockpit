using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The plugin's settings, persisted through the host's per-plugin <see cref="IPluginStorage"/>. Two modes:
/// the local GitHub CLI (<see cref="UseGitHubCli"/> — uses your existing <c>gh</c> login and shows open
/// pull requests across all repos for <see cref="GhOwner"/>), or a single repository over HTTP with an
/// optional token. The prompt template dropped on click is editable either way.
/// </summary>
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

    /// <summary>How many pull requests the inline section shows (1–20; default 5). The dialog always lists them all.</summary>
    public int MaxItems
    {
        get
        {
            var stored = storage.Get<int>("maxItems");
            return stored is >= 1 and <= 20 ? stored : 5;
        }
        set => storage.Set("maxItems", Math.Clamp(value, 1, 20));
    }

    /// <summary>Whether a pull request that starts waiting for your review raises a toast (default on). GitHub CLI mode only — the single-repo HTTP mode has no review-requested search.</summary>
    public bool NotifyOnReviewRequests
    {
        get => storage.Get<bool?>("notifyOnReviewRequests") ?? true;
        set => storage.Set("notifyOnReviewRequests", value);
    }

    /// <summary>
    /// The review requests already announced, as <c>owner/repo#number</c>. <c>null</c> means "never looked" —
    /// the first load primes this quietly instead of announcing every request that was already waiting.
    /// </summary>
    public IReadOnlySet<string>? SeenReviewRequests
    {
        get => storage.Get<string>("seenReviewRequests") is { } stored
            ? stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
            : null;
        set => storage.Set("seenReviewRequests", string.Join('\n', value ?? new HashSet<string>()));
    }

    /// <summary>
    /// Optional repository filter — one <c>owner/repo</c> per line (or comma-separated). When set, only pull
    /// requests in those repositories are shown; blank means all repositories (the default).
    /// </summary>
    public string RepoFilter
    {
        get => storage.Get<string>("repoFilter") ?? string.Empty;
        set => storage.Set("repoFilter", value);
    }

    /// <summary>The parsed <see cref="RepoFilter"/> as a set of <c>owner/repo</c> names, empty when no filter is set.</summary>
    public IReadOnlySet<string> RepoFilterSet =>
        RepoFilter
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
