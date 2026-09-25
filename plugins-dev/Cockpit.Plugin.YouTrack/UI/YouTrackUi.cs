using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.YouTrack.UI;

// The UI part of YouTrack (AC-1397): the issues dialog, the session header with its picker, and the settings. The
// backend part, YouTrackPlugin, holds the YouTrack client and the session links; this part reaches both over the
// plugin's channel through YouTrackBackend, and names the pane it acts for — its own, or the window's active one.
public sealed class YouTrackUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new YouTrackSettings(host.Storage);
        var backend = new YouTrackBackend(host.Channel);

        host.AddSettings(() => new YouTrackSettingsControl(host, settings));

        // 1280×860 (AC-297, up from 1040×700): the chips strip, fixed action toolbar and rendered description
        // all want more room than the old size gave them. PluginDialogHost clamps this against the cockpit's own
        // window size (94%), so a smaller screen still gets a dialog that fits rather than one cropped at 1280.
        void OpenIssues() =>
            // One dialog per plugin: reopening while it's up should refocus it, not stack a second one.
            _ = host.ShowDialogAsync("YouTrack Issues", () => new YouTrackDialogControl(settings, host, backend), "issues", width: 1280, height: 860);

        host.AddSideMenuButton("YouTrack", OpenIssues);
        host.AddSessionHeaderItem(session => new YouTrackSessionHeaderControl(host, session, backend, settings));

        // Picking a ticket is an action, so it lives in the header's one menu rather than in a button of its own — two
        // issue trackers meant two buttons asking the same question of a strip with room for neither.
        host.AddSessionHeaderAction(new PluginSessionAction(
            "Track a YouTrack issue…",
            "",
            session => YouTrackSessionHeaderControl.Pick(host, session, backend, settings))
        {
            IconKind = MaterialIconKind.TicketOutline,
        });

        // Same action on a keyboard shortcut (#: shortcuts) — the SDK's AddShortcut, shown in Options → Shortcuts.
        host.AddShortcut(new PluginShortcut("youtrack.open", "YouTrack issues", "Shift+Y", OpenIssues));
    }
}
