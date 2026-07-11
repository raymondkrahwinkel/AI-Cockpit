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
}
