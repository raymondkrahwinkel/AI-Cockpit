using Material.Icons;
using Cockpit.Plugin.GitHubPullRequests.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubPullRequests.UI;

// Registers the dock-rail panel (AC-960) with the widget's own view class. AC-1396: through ICockpitUiHost, where
// AddDockPanel is unconditional, so the older-host reflection guard is gone (as GitHubActions did in AC-1394).
internal static class PullRequestDockPanelRegistrar
{
    public static void Register(ICockpitUiHost host, GitHubPullRequestsSettings settings) =>
        host.AddDockPanel(new DockPanelRegistration(
            "github.pull-requests",
            "Pull Requests",
            MaterialIconKind.SourcePull,
            () => new GitHubPullRequestsWidget(settings, host, new _DockWidgetContext(host))));

    // Not built by IWidgetRegistry.CreateInstance — a fixed instance id, host.Storage unscoped (never collides
    // with a dashboard instance's "widget:{instanceId}:" keys), and a refresh signal that never fires, since
    // the rail panel has no manual-refresh control of its own.
    private sealed class _DockWidgetContext(ICockpitUiHost host) : IWidgetContext
    {
        public string InstanceId => "dock";

        public IPluginStorage Storage => host.Storage;

        // Nothing reads this since the widget's own session-signal debounce went (AC-1396).
        public ICockpitSessionObserver Sessions => NullCockpitSessionObserver.Instance;

        public event EventHandler? RefreshRequested { add { } remove { } }
    }
}
