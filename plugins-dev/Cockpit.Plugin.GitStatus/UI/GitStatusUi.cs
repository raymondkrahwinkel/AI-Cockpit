using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitStatus.UI;

// The UI part of Git status (AC-1390): the session-header badge and its settings view. The backend part,
// GitStatusPlugin, keeps the workflow steps and answers this part's git questions over the plugin's channel.
public sealed class GitStatusUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new GitStatusSettings(host.Storage);
        host.AddSettings(() => new GitStatusSettingsControl(host, settings));
        // In each session's own header rather than in the sidebar: the git state describes the repo that one
        // session works in, and a sidebar section following "whichever session is selected" says nothing about
        // the other panes on screen.
        host.AddSessionHeaderItem(session => new GitStatusHeaderControl(host, session, settings));
    }
}
