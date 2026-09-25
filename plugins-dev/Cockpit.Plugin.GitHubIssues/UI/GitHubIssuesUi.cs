using Material.Icons;
using Cockpit.Plugin.GitHubIssues.Contracts;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubIssues.UI;

// The UI part of GitHub Issues (AC-1396): the settings view, the left-menu button and its issues dialog, the session
// header item and the header's "Track a GitHub issue…" action. The backend part, GitHubIssuesPlugin, keeps the gh/HTTP
// clients and which issue each session is linked to, and answers this part's questions over the plugin's channel.
public sealed class GitHubIssuesUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // The same storage slice the backend part reads, so both see one set of settings.
        var settings = new GitHubIssuesSettings(host.Storage);

        host.AddSettings(() => new GitHubIssuesSettingsControl(host, settings));

        // 1280×860, up from 1040×700 — the chip, fixed action toolbar and rendered description all want more
        // room than the old size gave them, the same reasoning as the YouTrack dialog's resize. PluginDialogHost
        // clamps this against the cockpit's own window size, so a smaller screen still gets a dialog that fits.
        host.AddSideMenuButton(
            "GitHub Issues",
            // One dialog per plugin: reopening while it's up should refocus it, not stack a second one.
            () => _ = host.ShowDialogAsync("GitHub Issues", () => new GitHubIssuesDialogControl(settings, host), "issues", width: 1280, height: 860));

        // The issue this session is working on, in its own header — and, before one is picked, the way to pick it.
        host.AddSessionHeaderItem(session => new GitHubSessionHeaderControl(host, session, settings));

        host.AddSessionHeaderAction(new PluginSessionAction(
            "Track a GitHub issue…",
            "",
            session => GitHubSessionHeaderControl.Pick(host, session))
        {
            IconKind = MaterialIconKind.Github,
        });
    }
}
