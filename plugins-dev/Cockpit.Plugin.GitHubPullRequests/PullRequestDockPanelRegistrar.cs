using System.Runtime.CompilerServices;
using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubPullRequests;

// Registers the dock-rail panel (AC-960): its own IWidgetContext, but the same GitHubPullRequestsWidget view
// class host.AddWidget above uses. AddDockPanel/DockPanelRegistration are new in minHostVersion 0.24.0, so this
// is behind the same older-host guard PullRequestBadgeUpdater.cs uses for AddSideMenuButtonWithBadge.
internal static class PullRequestDockPanelRegistrar
{
    public static void Register(ICockpitHost host, GitHubPullRequestsSettings settings, PullRequestRefreshSource source)
    {
        try
        {
            _Register(host, settings, source);
        }
        catch (Exception exception) when (exception is MissingMethodException or MissingMemberException or TypeLoadException)
        {
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _Register(ICockpitHost host, GitHubPullRequestsSettings settings, PullRequestRefreshSource source) =>
        host.AddDockPanel(new DockPanelRegistration(
            "github.pull-requests",
            "Pull Requests",
            MaterialIconKind.SourcePull,
            () => new GitHubPullRequestsWidget(settings, host, new _DockWidgetContext(host), source)));

    // Not built by IWidgetRegistry.CreateInstance — a fixed instance id, host.Storage unscoped (never collides
    // with a dashboard instance's "widget:{instanceId}:" keys), and a refresh signal that never fires, since
    // the rail panel has no manual-refresh control of its own.
    private sealed class _DockWidgetContext(ICockpitHost host) : IWidgetContext
    {
        public string InstanceId => "dock";

        public IPluginStorage Storage => host.Storage;

        public ICockpitSessionObserver Sessions => host.Sessions;

        public event EventHandler? RefreshRequested { add { } remove { } }
    }
}
