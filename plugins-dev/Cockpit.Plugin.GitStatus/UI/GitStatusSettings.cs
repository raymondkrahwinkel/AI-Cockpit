using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus.UI;

// The header badge's one setting, in the plugin's storage. An install from before AC-522 may still carry the
// removed repository list under "repos"; nothing reads that key, so it is inert, and IPluginStorage cannot
// remove it.
internal sealed class GitStatusSettings(IPluginStorage storage)
{
    private const string ShowBranchNameKey = "showBranchName";

    // Raised when a display setting changes, so a live session-header badge can update at once without a restart.
    // Deliberately used instead of `ICockpitUiHost.OnSettingsSaved` (which has no unsubscribe): a per-session
    // header is transient, so it subscribes on attach and unsubscribes on detach — no dead control is left rooted.
    public event Action? Changed;

    // Whether the session-header badge shows the branch name next to the status dot (AC-36). Off leaves only the
    // coloured dot on screen — the branch stays in the tooltip — freeing header width. Defaults to on (dot + name).
    public bool ShowBranchName
    {
        get => storage.Get<bool?>(ShowBranchNameKey) ?? true;
        set
        {
            storage.Set(ShowBranchNameKey, value);
            Changed?.Invoke();
        }
    }
}
