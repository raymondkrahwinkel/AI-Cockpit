using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// The configured repository paths (#1), persisted as a JSON list in the plugin's per-plugin storage. The
/// dialog and the settings view both read/write through here; empty by default (the user adds their repos).
/// </summary>
internal sealed class GitStatusSettings(IPluginStorage storage)
{
    private const string ReposKey = "repos";
    private const string ShowBranchNameKey = "showBranchName";

    public IReadOnlyList<string> Repos => storage.Get<List<string>>(ReposKey) ?? [];

    public void SaveRepos(IReadOnlyList<string> repos) => storage.Set(ReposKey, new List<string>(repos));

    /// <summary>
    /// Whether the session-header badge shows the branch name next to the status dot (AC-36). Off leaves only the
    /// coloured dot on screen — the branch stays in the tooltip — freeing header width. Defaults to on (dot + name).
    /// </summary>
    public bool ShowBranchName
    {
        get => storage.Get<bool?>(ShowBranchNameKey) ?? true;
        set => storage.Set(ShowBranchNameKey, value);
    }
}
