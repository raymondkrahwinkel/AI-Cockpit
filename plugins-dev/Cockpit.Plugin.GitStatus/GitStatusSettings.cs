using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// The session-header indicator's one setting, persisted in the plugin's per-plugin storage.
/// <para>
/// AC-522 removed the plugin's dialog, and with it the only reader/writer of a manually configured repository
/// list this class used to keep under a "repos" key. Nothing here reads that key any more; an install from
/// before AC-522 may still carry it in storage, but it is inert — never deserialized, so it cannot fail to
/// load. <see cref="IPluginStorage"/> has no way to remove a stored key, so the JSON entry itself may outlive
/// the feature, harmlessly.
/// </para>
/// </summary>
internal sealed class GitStatusSettings(IPluginStorage storage)
{
    private const string ShowBranchNameKey = "showBranchName";

    /// <summary>
    /// Raised when a display setting changes, so a live session-header badge can update at once without a restart.
    /// Deliberately used instead of <c>ICockpitHost.OnSettingsSaved</c> (which has no unsubscribe): a per-session
    /// header is transient, so it subscribes on attach and unsubscribes on detach — no dead control is left rooted.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Whether the session-header badge shows the branch name next to the status dot (AC-36). Off leaves only the
    /// coloured dot on screen — the branch stays in the tooltip — freeing header width. Defaults to on (dot + name).
    /// </summary>
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
