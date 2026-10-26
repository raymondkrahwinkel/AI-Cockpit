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

    // Uren · Dag and Overzicht with fixed data, for `--screenshot --scene plugin:<name>` (AC-1766).
    public static IReadOnlyDictionary<string, Func<(WorkspaceTypeRegistration Registration, string? StatusText)>> WorkspaceScenes => ProductivityScenes.Workspaces;

    private ICockpitUiHost? _host;
    private ProductivityController? _controller;
    private OverviewController? _overviewController;
    private IDisposable? _reports;

    public void InitializeUi(ICockpitUiHost host)
    {
        _host = host;
        _controller = new ProductivityController(host.Channel, TimeProvider.System);
        _overviewController = new OverviewController(host.Channel, TimeProvider.System);
        _reports = host.Channel.Subscribe("productivity.hours-reported", message => { _ = _RefreshAsync(host, _controller); });
        _controller.UpdateProjectFallback(host.ActiveProjectId);
        var badge = host.AddSideMenuButtonWithBadge("Uren", () => _Open(host), MaterialIconKind.ClockOutline);
        badge.Tone = SideMenuButtonBadgeTone.Success;
        _controller.AttachBadge(badge);
        host.AddSideMenuButton("Overzicht", () => _OpenOverview(host), MaterialIconKind.ViewDashboardOutline);
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
            _ => new OverviewWorkspaceBody(_overviewController, () => _Open(host)))
        {
            IconKind = MaterialIconKind.ViewDashboardOutline,
            Description = "Bekijk jouw tijd, agents, tokens en kosten per project, ticket en sessie.",
        });
        host.ActiveSessionChanged += _ActiveSessionChanged;
        _ = _RefreshAsync(host, _controller);
    }

    public void Dispose()
    {
        _reports?.Dispose();
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
            controller.UpdateProjectFallback(host.ActiveProjectId);
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
        try
        {
            await controller.RefreshAsync();
            if (controller.Error is { } error)
            {
                host.ShowToast($"Uren kon niet verversen: {error}", PluginToastSeverity.Error);
            }
        }
        catch (Exception exception)
        {
            host.ShowToast($"Uren kon niet verversen: {exception.Message}", PluginToastSeverity.Error);
        }
    }
}
