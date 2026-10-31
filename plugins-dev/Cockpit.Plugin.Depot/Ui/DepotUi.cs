using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Material.Icons;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Onboarding;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Depot.UI;

// The UI part of Depot (AC-1394): the settings view and the global-toolbar shortcut to it. The backend part,
// DepotPlugin, keeps the project-memory-source/shared-project-source/MCP-provider registrations and syncs them
// whenever this part reports a connection-list save over the plugin's channel.
public sealed class DepotUi : ICockpitPluginUi, IFirstStartPluginUi
{
    // Read by Cockpit's `--screenshot --scene plugin:<name>` on a dev checkout only; not a plugin contract.
    public static IReadOnlyDictionary<string, Func<Control>> Scenes => DepotScenes.All;

    public static IReadOnlyDictionary<string, Func<(WorkspaceTypeRegistration Registration, string? StatusText)>> WorkspaceScenes => DepotScenes.Workspaces;

    // AC-1509: route C, there from the very first start; the wizard runs before InitializeUi.
    public IReadOnlyList<FirstStartRoute> CreateFirstStartRoutes(IFirstStartHost host) =>
    [
        new(
            "depot.sign-in",
            "Sign in to a Depot",
            "Restore your latest backup, or set up from your profile and your organisation.",
            () => new DepotFirstStartView(host))
        {
            IconKind = MaterialIconKind.Cloud,
        },
    ];

    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new DepotSettings(host.Storage);
        host.AddFileWorkspaceType(DepotMarkdownFiles.WorkspaceTypeId, "Markdown file");
        host.AddWorkspaceType(new WorkspaceTypeRegistration(ArtifactWorkspace.WorkspaceTypeId, "Depot file", context => new ArtifactWorkspace(host, context, settings.Connections))
        {
            IconKind = MaterialIconKind.FileOutline,
        });
        host.AddWorkspaceType(DepotSharedNote.Registration(host, () => settings.Connections));
        host.AddSettings(() => new DepotSettingsControl(host, settings));
        // AC-1511: Admin → Plugins → Depot → Settings on a server, whose backend part this channel reaches.
        host.AddServerSettings(channel => new DepotServerSettingsControl(channel));
        host.Channel.Subscribe(DepotChannel.OpenSettings, evt => Dispatcher.UIThread.Post(() =>
        {
            // Channel handlers are synchronous, so the wrapper observes and logs the detached settings task.
            _ = _ShowSettingsAsync(host);
        }));
        // AC-784: same global-toolbar route the Kubernetes plugin uses (AC-91) — the project editor's own
        // "Servers…" button above still opens settings the same way it always has.
        host.AddToolbarAction(new ToolbarAction("Depot settings", MaterialIconKind.Database, () => host.ShowSettingsAsync()));
        // AC-1586: one inbox model for the plugin's lifetime, so the side-menu badge sums every connection's unread.
        var inbox = new DepotInboxModel(host);
        // AC-1581: one route into a note for the toast, the inbox, the Notes tab and a transcript link.
        var route = new DepotNoteRoute(host, () => settings.Connections,
            (connection, token) => DepotNoteRoute.ResolveOverChannelAsync(host, connection, token),
            inbox.SignInAsync, (connection, messageId, show) => inbox.JoinedAsync(connection.Id, messageId, show), _LaunchNoteAsync);
        // AC-1580's opener: the note's own key, so a second Join brings its tab forward and Show tab is the same open.
        route.OpenLive = async (connection, target) =>
        {
            var project = $"{target.Organization}/{target.Project}";
            var edit = target.Stand == DepotNoteStand.Edit;
            await DepotMarkdownFiles.OpenNoteAsync(host, connection, project, target.Path, edit).ConfigureAwait(true);
            return () => DepotMarkdownFiles.OpenAsync(host, connection, project, target.Path, edit);
        };
        inbox.Join = route.JoinInvitationAsync;
        var invitations = new DepotInvitationNotifications(host, () => settings.Connections, route.JoinInvitationAsync);
        inbox.Changed += page => Dispatcher.UIThread.Post(() => invitations.Show(page));
        var notes = new DepotNotesModel(host, inbox)
        {
            NewMeetingNote = (connection, _) => DepotMeetingNotes.ShowAsync(host, [connection]),
            JoinNote = route.JoinLiveAsync,
        };
        var badge = host.AddSideMenuButtonWithBadge("Depot inbox", () =>
        {
            // Side-menu callbacks are synchronous, so the wrapper observes and logs the detached dialog task.
            _ = _ShowInboxAsync(host, settings, inbox, notes);
        }, MaterialIconKind.InboxOutline);
        inbox.Changed += _ => badge.Primary = inbox.Total;
        // Startup continues while counts load; LoadCountsAsync logs failures and a later push can refresh the badge.
        _ = inbox.LoadCountsAsync();

        // AC-1568: the file browser, as a dock panel and from the toolbar.
        var liveBadge = new DockPanelBadge { Tone = SideMenuButtonBadgeTone.Success };
        notes.Changed += _ => Dispatcher.UIThread.Post(() =>
        {
            liveBadge.Count = notes.Total;
            host.SetStatusText($"● {notes.Total} live notes · Depot", SideMenuButtonBadgeTone.Success);
        });
        host.AddDockPanel(new DockPanelRegistration("depot-browser", "Depot", MaterialIconKind.FolderNetworkOutline,
            () => new DepotBrowserPanel(host, settings.Connections, inbox, notes: notes)) { Badge = liveBadge });
        _ = notes.LoadAsync(settings.Connections);
        host.AddLinkHandler(new LinkHandlerRegistration(
            (string link, out int start, out int length) => DepotTranscriptLinks.TryMatch(link, settings.Connections.Select(connection => connection.Url).ToList(), out start, out length),
            (link, modifiers) =>
            {
                // A link handler is synchronous, so the wrapper observes and logs the detached task.
                _ = DepotTranscriptLinks.IsNoteLink(link) ? _JoinLinkAsync(route, link) : _OpenBrowserAsync(host);
                return true;
            }));
        host.AddToolbarAction(new ToolbarAction("Depot", MaterialIconKind.FolderNetworkOutline,
            () => host.ShowDialogAsync("Depot", () => new DepotBrowserPanel(host, settings.Connections, inbox, notes: notes), "depot-browser", 1000, 680)));
    }

    private static Task _LaunchNoteAsync(Uri uri) =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? window.Launcher.LaunchUriAsync(uri)
            : Task.FromException(new InvalidOperationException("The Cockpit window is unavailable."));

    private static async Task _JoinLinkAsync(DepotNoteRoute route, string link)
    {
        try
        {
            await route.JoinLinkAsync(link).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Depot: joining a note from a transcript link failed: {exception.Message}");
        }
    }

    // Started from a link click, which cannot await it: a host that cannot reach its UI thread is logged, not left unobserved.
    private static async Task _OpenBrowserAsync(ICockpitUiHost host)
    {
        try
        {
            await host.OpenDockPanelAsync("depot-browser").ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Depot: opening the file browser from a transcript link failed: {exception}");
        }
    }

    private static async Task _ShowSettingsAsync(ICockpitUiHost host)
    {
        try
        {
            await host.ShowSettingsAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Opening Depot settings failed: {exception}");
        }
    }

    private static async Task _ShowInboxAsync(ICockpitUiHost host, DepotSettings settings, DepotInboxModel inbox, DepotNotesModel notes)
    {
        try
        {
            await host.ShowDialogAsync(
                "Depot", () => new DepotBrowserPanel(host, settings.Connections, inbox, openInbox: true, notes: notes), "depot-browser", 1000, 680);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Opening the Depot inbox failed: {exception}");
        }
    }
}
