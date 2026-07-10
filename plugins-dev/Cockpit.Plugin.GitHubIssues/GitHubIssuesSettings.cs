using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The plugin's settings, persisted through the host's per-plugin <see cref="IPluginStorage"/> (its slice
/// of cockpit.json): which repository to read, an optional token (for private repos / higher rate limits)
/// and the editable prompt template. Each property reads and writes storage directly, so the Options tab
/// and the issue list always see the same values.
/// </summary>
internal sealed class GitHubIssuesSettings(IPluginStorage storage)
{
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
