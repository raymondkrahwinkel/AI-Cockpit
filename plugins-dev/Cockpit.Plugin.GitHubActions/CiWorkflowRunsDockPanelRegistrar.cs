using System.Runtime.CompilerServices;
using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubActions;

// Registers the dock-rail panel (AC-1065), the same mechanism PullRequestDockPanelRegistrar uses for the pull-requests
// plugin. AddDockPanel/DockPanelRegistration are new in minHostVersion 0.24.0, so this is behind the same
// older-host guard.
internal static class CiWorkflowRunsDockPanelRegistrar
{
    public static void Register(ICockpitHost host)
    {
        try
        {
            _Register(host);
        }
        catch (Exception exception) when (exception is MissingMethodException or MissingMemberException or TypeLoadException)
        {
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _Register(ICockpitHost host) =>
        host.AddDockPanel(new DockPanelRegistration(
            "github.actions",
            "GitHub Actions",
            MaterialIconKind.Cog,
            () => new CiWorkflowRunsWidget(new _DockWidgetContext(host))));

    // Not built by IWidgetRegistry.CreateInstance — a fixed instance id, host.Storage unscoped (never collides
    // with a dashboard instance's "widget:{instanceId}:" keys), and a refresh signal that never fires, since
    // the rail panel has no manual-refresh control of its own — the widget's own timer covers it.
    private sealed class _DockWidgetContext(ICockpitHost host) : IWidgetContext
    {
        public string InstanceId => "dock";

        public IPluginStorage Storage => host.Storage;

        public ICockpitSessionObserver Sessions => host.Sessions;

        public event EventHandler? RefreshRequested { add { } remove { } }
    }
}
