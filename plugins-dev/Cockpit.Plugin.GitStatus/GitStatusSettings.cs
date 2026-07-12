using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// The configured repository paths (#1), persisted as a JSON list in the plugin's per-plugin storage. The
/// dialog and the settings view both read/write through here; empty by default (the user adds their repos).
/// </summary>
internal sealed class GitStatusSettings(IPluginStorage storage)
{
    private const string ReposKey = "repos";

    public IReadOnlyList<string> Repos => storage.Get<List<string>>(ReposKey) ?? [];

    public void SaveRepos(IReadOnlyList<string> repos) => storage.Set(ReposKey, new List<string>(repos));
}
