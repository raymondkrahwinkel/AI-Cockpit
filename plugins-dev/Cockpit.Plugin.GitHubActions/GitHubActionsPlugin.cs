using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubActions;

// GitHub Actions CI status (AC-52): adds an indicator to each session's header showing the latest workflow-run status
// of the branch that session is working in — green pass, red fail, amber running — click to open the run on GitHub.
// Completes the GitHub set (Issues + Pull Requests + Git status → + CI). Uses the machine's existing `gh` login;
// no local state, so `ConfigureServices` is empty.
//
// AC-1065: a Dashboard widget and a dock-rail panel — same pattern as the pull-requests plugin's own pair — showing
// the branch's recent runs (workflow, branch, status, duration) kept open next to your work, each placed instance
// with its own per-instance run count (CiWorkflowRunsWidgetConfig). Read-only: no restart/cancel, that is a change
// and belongs behind its own consent-gated ticket.
public sealed class GitHubActionsPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "github-actions",
        DisplayName: "GitHub Actions",
        Author: "Cockpit",
        Description: "Shows the GitHub Actions status of the branch a session is working in, in that session's header: "
            + "a coloured icon (green pass, red fail, amber running) for the latest workflow run on the current branch, "
            + "with the workflow/event/time on hover — click to open the run on GitHub. Also offers a Dashboard widget "
            + "and a dock-rail panel, each listing the branch's recent runs with the same colours plus duration, kept "
            + "open next to your work; read-only, with its own per-instance run count (1–20). Requires the gh CLI "
            + "installed and authenticated on the machine running Cockpit.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services — the header indicator, widget and dock panel all read gh on demand.
    }

    public void Initialize(ICockpitHost host)
    {
        // In each session's own header rather than the sidebar: CI status describes the branch that one session is on,
        // the same reasoning the git-status badge follows.
        host.AddSessionHeaderItem(session => new CiStatusHeaderControl(session));

        // AC-1065: the same status, as a list for a workspace given over to it — mirrors the pull-requests widget.
        // WidgetRegistration.CreateConfigView postdates minHostVersion 0.1.0 (unlike the header item above), so this
        // is behind the same older-host guard the dock-panel registrar below uses — an older host keeps the header
        // and simply never gets the widget, rather than failing Initialize and losing both.
        try
        {
            _RegisterWidget(host);
        }
        catch (Exception exception) when (exception is MissingMethodException or MissingMemberException or TypeLoadException)
        {
        }

        // AC-1065: the same list, reachable as a dock-rail panel too, next to the header dot.
        CiWorkflowRunsDockPanelRegistrar.Register(host);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _RegisterWidget(ICockpitHost host) =>
        host.AddWidget(new WidgetRegistration("widgets.github-actions", "GitHub Actions", context => new CiWorkflowRunsWidget(context))
        {
            IconKind = MaterialIconKind.Cog,
            Description = "The branch's recent GitHub Actions runs, with a configurable count.",
            DefaultColumnSpan = 6,
            DefaultRowSpan = 8,
            CreateConfigView = context => new CiWorkflowRunsWidgetSettingsView(context),
        });

    public void Dispose()
    {
    }
}
