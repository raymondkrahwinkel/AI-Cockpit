using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubActions.UI;

// Registers the dock-rail panel (AC-1065), the same mechanism PullRequestDockPanelRegistrar uses for the pull-requests
// plugin.
//
// AC-1394: takes ICockpitUiHost directly rather than ICockpitHost — AddDockPanel is an unconditional member of the
// UI host contract (unlike the old ICockpitHost, which a pre-AC-1065 host could predate), so the reflection guard
// this used to need no longer applies.
internal static class CiWorkflowRunsDockPanelRegistrar
{
    public static void Register(ICockpitUiHost host) =>
        host.AddDockPanel(new DockPanelRegistration(
            "github.actions",
            "GitHub Actions",
            MaterialIconKind.Cog,
            () => new CiWorkflowRunsWidget(new _DockWidgetContext(host), host)));

    // Not built by IWidgetRegistry.CreateInstance — a fixed instance id, host.Storage unscoped (never collides
    // with a dashboard instance's "widget:{instanceId}:" keys), and a refresh signal that never fires, since
    // the rail panel has no manual-refresh control of its own — the widget's own timer covers it.
    private sealed class _DockWidgetContext(ICockpitUiHost host) : IWidgetContext
    {
        public string InstanceId => "dock";

        public IPluginStorage Storage => host.Storage;

        // The widget itself follows the window's active session through the host directly (see CiWorkflowRunsWidget);
        // nothing reads this. NullCockpitSessionObserver keeps the interface satisfied without inventing a stub.
        public ICockpitSessionObserver Sessions => NullCockpitSessionObserver.Instance;

        public event EventHandler? RefreshRequested { add { } remove { } }
    }
}
