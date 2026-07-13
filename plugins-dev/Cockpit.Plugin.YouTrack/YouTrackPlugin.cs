using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

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
public sealed class YouTrackPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "youtrack",
        DisplayName: "YouTrack",
        Version: "1.5.0",
        Author: "Cockpit",
        Description: "Browse open issues across one or more configured YouTrack instances (over HTTP with a permanent token per instance — YouTrack has no CLI), with instance/project/state filters and an \"Assigned to me\" filter, and drop a prompt asking the agent to work on one. Opens from the left menu or the Shift+Y shortcut. Run the ticket workflow from the cockpit: Start an issue (move it to in progress, assign it to you, tie it to the session you work in), move it to any state the board itself allows — including workflow-governed boards, whose allowed transitions are read rather than assumed — and see the linked issue with its status in that session's header, with quick actions. The prompt template is editable in settings. Also registers each instance's JetBrains remote MCP server so sessions can query YouTrack directly as tools.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new YouTrackSettings(host.Storage);

        // One registry, shared by the dialog (which links an issue to the active session) and the header items
        // (each of which shows the issue linked to its own session) — see SessionIssueLinks.
        var links = new SessionIssueLinks();

        host.AddSettings(() => new YouTrackSettingsControl(settings));

        void OpenIssues() =>
            _ = host.ShowDialogAsync("YouTrack Issues", () => new YouTrackDialogControl(settings, host, links), 1040, 700);

        host.AddSideMenuButton("YouTrack", OpenIssues);
        host.AddSessionHeaderItem(session => new YouTrackSessionHeaderControl(host, session, links));
        // Same action on a keyboard shortcut (#: shortcuts) — the SDK's AddShortcut, shown in Options → Shortcuts.
        host.AddShortcut(new PluginShortcut("youtrack.open", "YouTrack issues", "Shift+Y", OpenIssues));

        _RegisterMcpServers(host, settings);
        host.OnSettingsSaved(() => _RegisterMcpServers(host, settings));
    }

    public void Dispose()
    {
    }

    // Fire-and-forget (#60): host.AddMcpServer persists to disk, but Initialize and the OnSettingsSaved
    // callback are both synchronous contribution points, same pattern as ShowDialogAsync above.
    private static void _RegisterMcpServers(ICockpitHost host, YouTrackSettings settings)
    {
        foreach (var contribution in YouTrackMcpRegistration.BuildContributions(settings.Instances))
        {
            _ = host.AddMcpServer(contribution);
        }
    }
}
