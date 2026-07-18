using Microsoft.Extensions.DependencyInjection;
using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Plugin #42, reworked in #48 to mirror the GitHub Issues plugin: a left-menu button (not an always-visible
/// inline section) opens a dialog listing open issues across one or more configured YouTrack instances, with
/// instance/project/state filters. Unlike the GitHub plugins, YouTrack has no local CLI equivalent to <c>gh</c>,
/// so this plugin is HTTP-only: each instance is a base URL + permanent token (see settings) — no
/// CLI-vs-HTTP toggle. Clicking an issue in the dialog injects the rendered template into the active session
/// so the agent picks it up, falling back to the clipboard when there is no active session. Its settings live
/// in the host's per-plugin storage, so <see cref="ConfigureServices"/> is empty. Also registers the
/// JetBrains remote MCP server (#60) for every fully-configured instance, on <see cref="Initialize"/> and
/// again whenever settings are saved — see <see cref="YouTrackMcpRegistration"/>.
/// </summary>
public sealed class YouTrackPlugin : ICockpitPlugin, IPluginMcpProvider
{
    // The instances live in the host's per-plugin storage; kept here from Initialize so GetMcpServers can read the
    // current set each time the host asks (AC-11), rather than a snapshot taken once.
    private YouTrackSettings? _settings;

    public PluginMetadata Metadata { get; } = new(
        Id: "youtrack",
        DisplayName: "YouTrack",
        Version: "1.14.0",
        Author: "Cockpit",
        Description: "Browse open issues across one or more configured YouTrack instances (over HTTP with a permanent token per instance — YouTrack has no CLI), with instance/project/state filters and an \"Assigned to me\" filter, and drop a prompt asking the agent to work on one. Opens from the left menu or the Shift+Y shortcut. Run the ticket workflow from the cockpit: Start an issue (move it to in progress, assign it to you, tie it to the session you work in), move it to any state the board itself allows — including workflow-governed boards, whose allowed transitions are read rather than assumed — and see the linked issue with its status in that session's header, with quick actions. The prompt template is editable in settings. Also registers each instance's JetBrains remote MCP server so sessions can query YouTrack directly as tools, and contributes three workflow steps — a ticket picked for a session, a ticket whose status you moved, and a step that moves one — so a flow can run the ticket half of your working day.");

    public void ConfigureServices(IServiceCollection services)
    {
        // Register this plugin as the source of its own MCP servers (AC-11): the host's McpServerCatalog injects
        // every IPluginMcpProvider and asks each when it assembles a session, so the plugin owns its MCP config
        // rather than pushing it into the shared registry. Same instance the host initializes, so GetMcpServers
        // reads the settings this plugin later loads in Initialize.
        services.AddSingleton<IPluginMcpProvider>(this);
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new YouTrackSettings(host.Storage);
        _settings = settings;

        // One registry, shared by the dialog (which links an issue to the active session) and the header items
        // (each of which shows the issue linked to its own session) — see SessionIssueLinks.
        var links = new SessionIssueLinks();

        // And one bus for the moves themselves, shared by the two places a ticket can be moved from — see IssueStateChanges.
        var stateChanges = new IssueStateChanges();

        host.AddSettings(() => new YouTrackSettingsControl(settings));

        void OpenIssues() =>
            _ = host.ShowDialogAsync("YouTrack Issues", () => new YouTrackDialogControl(settings, host, links, stateChanges), 1040, 700);

        host.AddSideMenuButton("YouTrack", OpenIssues);

        // What a flow can do with a ticket (#69): the workflow plugin knows nothing about YouTrack, and should not.
        // It knows that someone offers a trigger called youtrack.picked and a step that sets a status.
        foreach (var step in YouTrackWorkflowSteps.All(settings))
        {
            host.AddWorkflowStep(step);
        }

        // And the flows those steps are for: how they fit together is knowledge this plugin has and an empty canvas
        // does not.
        foreach (var template in YouTrackWorkflowTemplates.All)
        {
            host.AddWorkflowTemplate(template);
        }

        // And the trigger is fired by the act it names: you picked a ticket for a session. A trigger nobody fires is
        // worse than a trigger nobody offers.
        links.Linked += (_, linked) => host.RaiseWorkflowTrigger(
            YouTrackWorkflowSteps.PickedTrigger,
            new Dictionary<string, string>
            {
                ["ticket"] = linked.Link.Issue.IdReadable,
                ["summary"] = linked.Link.Issue.Summary,
                ["branch"] = BranchName.From(linked.Link.Issue.IdReadable, linked.Link.Issue.Summary, settings.BranchPattern),
                ["state"] = linked.Link.Issue.State ?? string.Empty,
                ["directory"] = linked.WorkingDirectory ?? string.Empty,
            });

        // The same rule for the second trigger: it fires on the move itself, wherever the operator made it.
        stateChanges.Changed += (_, moved) => host.RaiseWorkflowTrigger(
            YouTrackWorkflowSteps.StatusChangedTrigger,
            new Dictionary<string, string>
            {
                ["ticket"] = moved.Issue.IdReadable,
                ["summary"] = moved.Issue.Summary,
                ["state"] = moved.NewState,
                ["previous_state"] = moved.PreviousState,
                ["branch"] = BranchName.From(moved.Issue.IdReadable, moved.Issue.Summary, settings.BranchPattern),
                ["directory"] = moved.WorkingDirectory ?? string.Empty,
            });

        host.AddSessionHeaderItem(session => new YouTrackSessionHeaderControl(host, session, links, settings, stateChanges));

        // AC-14: when a session tracks an issue and the operator turned the option on (the header menu's checkable
        // "Attach sent images to this issue"), an image sent with a message is also attached to that issue. The host
        // routes the images generically; this handler does the YouTrack-specific upload.
        host.AddSessionImageSink(new SessionImageSinkRegistration(dispatch => _AttachSentImagesAsync(host, links, dispatch)));

        // Picking a ticket is an action, so it lives in the header's one menu rather than in a button of its own — two
        // issue trackers meant two buttons asking the same question of a strip with room for neither.
        host.AddSessionHeaderAction(new PluginSessionAction(
            "Track a YouTrack issue…",
            "",
            session => YouTrackSessionHeaderControl.Pick(host, session, links, settings))
        {
            IconKind = MaterialIconKind.TicketOutline,
        });
        // Same action on a keyboard shortcut (#: shortcuts) — the SDK's AddShortcut, shown in Options → Shortcuts.
        host.AddShortcut(new PluginShortcut("youtrack.open", "YouTrack issues", "Shift+Y", OpenIssues));

        // AC-11: the plugin no longer pushes its MCP servers into the shared registry — the host asks for them
        // through GetMcpServers when a session is assembled. Reclaim what an earlier version pushed, so those
        // entries leave the MCP-servers manager and this plugin is their sole owner from here on.
        foreach (var name in YouTrackMcpRegistration.ManagedServerNames(settings.Instances))
        {
            _ = host.RemoveMcpServer(name);
        }
    }

    /// <summary>
    /// The MCP servers this plugin provides (AC-11): one per fully-configured, opted-in instance. Read live from
    /// storage each time, so a URL, token, or the per-instance toggle the operator changes takes effect on the
    /// next session without this plugin having to keep any other store in sync.
    /// </summary>
    public IReadOnlyList<McpServerContribution> GetMcpServers() =>
        _settings is null ? [] : YouTrackMcpRegistration.BuildContributions(_settings.Instances);

    // Attaches the images from one message to the pane's tracked issue (AC-14), when that pane opted in. A no-op
    // when the pane tracks nothing or the option is off; each image is uploaded independently, so one failure does
    // not stop the rest, and the operator is told either way.
    private static async Task _AttachSentImagesAsync(ICockpitHost host, SessionIssueLinks links, SessionImageDispatch dispatch)
    {
        if (!links.AttachesImages(dispatch.PaneId) || links.For(dispatch.PaneId) is not { } link)
        {
            return;
        }

        var client = new YouTrackClient();
        var attached = 0;
        foreach (var image in dispatch.Images)
        {
            try
            {
                var bytes = Convert.FromBase64String(image.Base64Data);
                await client.AttachFileAsync(link.Instance.InstanceUrl, link.Instance.Token, link.Issue.IdReadable, image.SuggestedFileName, bytes, image.MediaType, CancellationToken.None);
                attached++;
            }
            catch (Exception exception)
            {
                host.ShowToast($"{link.Issue.IdReadable}: could not attach an image — {exception.Message}", PluginToastSeverity.Error);
            }
        }

        if (attached > 0)
        {
            host.ShowToast($"Attached {attached} image{(attached == 1 ? "" : "s")} to {link.Issue.IdReadable}.", PluginToastSeverity.Success);
        }
    }

    public void Dispose()
    {
    }
}
