using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

// The plugin's settings, persisted through the host's per-plugin `IPluginStorage`. Two modes:
// the local GitHub CLI (`UseGitHubCli` — uses your existing `gh` login and shows open
// issues across all repos for `GhOwner`), or a single repository over HTTP with an optional
// token. The prompt template dropped on click is editable either way.
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

    // The label this operator's repos use for work in flight ("in progress", "status: in progress"). Empty — the default — means the menus simply do not offer it: GitHub enforces no convention, and offering a label that does not exist would fail on the click.
    public string InProgressLabel
    {
        get => storage.Get<string>("inProgressLabel") ?? string.Empty;
        set => storage.Set("inProgressLabel", value);
    }

    // Extra GitHub search terms for the session picker — "-label:blocked", "label:bug", "no:assignee". Empty by
    // default. The picker already asks only for open issues: a closed issue is work that is over, and offering it is
    // offering to start something that finished.
    public string PickerTerms
    {
        get => storage.Get<string>("pickerTerms") ?? string.Empty;
        set => storage.Set("pickerTerms", value);
    }

    // How a branch is named for an issue — `{number}` and `{title}`. Default `{number}-{title}`; `feature/{number}` works too. A naming convention is a team's business; that the result is a ref git accepts is this plugin's.
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
