using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The plugin's settings, persisted through the host's per-plugin <see cref="IPluginStorage"/>. Two modes:
/// the local GitHub CLI (<see cref="UseGitHubCli"/> — uses your existing <c>gh</c> login and shows open
/// issues across all repos for <see cref="GhOwner"/>), or a single repository over HTTP with an optional
/// token. The prompt template dropped on click is editable either way.
/// </summary>
internal sealed class GitHubIssuesSettings(IPluginStorage storage)
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

    /// <summary>The label this operator's repos use for work in flight ("in progress", "status: in progress"). Empty — the default — means the menus simply do not offer it: GitHub enforces no convention, and offering a label that does not exist would fail on the click.</summary>
    public string InProgressLabel
    {
        get => storage.Get<string>("inProgressLabel") ?? string.Empty;
        set => storage.Set("inProgressLabel", value);
    }

    /// <summary>
    /// Extra GitHub search terms for the session picker — "-label:blocked", "label:bug", "no:assignee". Empty by
    /// default. The picker already asks only for open issues: a closed issue is work that is over, and offering it is
    /// offering to start something that finished.
    /// </summary>
    public string PickerTerms
    {
        get => storage.Get<string>("pickerTerms") ?? string.Empty;
        set => storage.Set("pickerTerms", value);
    }

    /// <summary>How a branch is named for an issue — <c>{number}</c> and <c>{title}</c>. Default <c>{number}-{title}</c>; <c>feature/{number}</c> works too. A naming convention is a team's business; that the result is a ref git accepts is this plugin's.</summary>
    public string BranchPattern
    {
        get => storage.Get<string>("branchPattern") is { Length: > 0 } pattern ? pattern : GitHubBranchName.DefaultPattern;
        set => storage.Set("branchPattern", value);
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
}
