using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;

namespace Cockpit.Plugin.Productivity.UI;

public sealed class ProductivityUi : ICockpitPluginUi, IDisposable
{
    public const string WorkspaceTypeId = "workspace.productivity.hours";
    public const string OverviewWorkspaceTypeId = "workspace.productivity.overview";

    private ICockpitUiHost? _host;
    private ProductivityController? _controller;
    private OverviewController? _overviewController;

    public void InitializeUi(ICockpitUiHost host)
    {
        _host = host;
        _controller = new ProductivityController(host.Channel, TimeProvider.System);
        _overviewController = new OverviewController(host.Channel, TimeProvider.System);
        _controller.UpdateProjectFallback(host.ActiveSessionWorkingDirectory);
        var badge = host.AddSideMenuButtonWithBadge("Uren", () => _Open(host), MaterialIconKind.ClockOutline);
        badge.Tone = SideMenuButtonBadgeTone.Success;
        _controller.AttachBadge(badge);
        host.AddSideMenuButton("Overzicht", () => _OpenOverview(host));
        host.AddSideMenuSection("Tijd", () => new TimeSidebar(_controller, () => _Open(host)));
        host.AddWorkspaceType(new WorkspaceTypeRegistration(
            WorkspaceTypeId,
            "Uren",
            _ => new HoursWorkspaceBody(_controller, host))
        {
            IconKind = MaterialIconKind.ClockOutline,
            Description = "Leg je eigen uren vast met een duur of stopwatch en bekijk ze per dag.",
        });
        host.AddSettings(() => new ProductivitySettingsControl(host, _controller));
        host.AddWorkspaceType(new WorkspaceTypeRegistration(
            OverviewWorkspaceTypeId,
            "Overzicht",
            _ => new OverviewWorkspaceBody(_overviewController))
        {
            IconKind = MaterialIconKind.ViewDashboardOutline,
            Description = "Bekijk jouw tijd, agents, tokens en kosten per project, ticket en sessie.",
        });
        host.ActiveSessionChanged += _ActiveSessionChanged;
        _ = _RefreshAsync(host, _controller);
    }

    public void Dispose()
    {
        if (_host is { } host)
        {
            host.ActiveSessionChanged -= _ActiveSessionChanged;
        }

        _controller?.Dispose();
        _overviewController?.Dispose();
    }

    private void _ActiveSessionChanged(object? sender, EventArgs eventArgs)
    {
        if (_host is { } host && _controller is { } controller)
        {
            controller.UpdateProjectFallback(host.ActiveSessionWorkingDirectory);
        }
    }

    private static async void _Open(ICockpitUiHost host)
    {
        try
        {
            await host.OpenWorkspaceAsync(WorkspaceTypeId);
        }
        catch (Exception exception)
        {
            host.ShowToast($"Uren kon niet openen: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private static async void _OpenOverview(ICockpitUiHost host)
    {
        try
        {
            await host.OpenWorkspaceAsync(OverviewWorkspaceTypeId);
        }
        catch (Exception exception)
        {
            host.ShowToast($"Overzicht kon niet openen: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private static async Task _RefreshAsync(ICockpitUiHost host, ProductivityController controller)
    {
        await controller.RefreshAsync();
        if (controller.Error is { } error)
        {
            host.ShowToast($"Uren kon niet verversen: {error}", PluginToastSeverity.Error);
        }
    }
}
