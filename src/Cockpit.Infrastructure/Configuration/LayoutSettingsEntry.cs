using Cockpit.Core.Layout;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `LayoutSettings` in the `layout` section of `cockpit.json`.
internal sealed class LayoutSettingsEntry
{
    public bool SingleSessionLayout { get; set; }

    public bool StackSessionsVertically { get; set; }

    public bool MinimizeToTrayOnClose { get; set; }

    public double SidebarWidth { get; set; } = LayoutSettings.DefaultSidebarWidth;

    public bool SidebarCollapsed { get; set; }

    public double FocusRailWeight { get; set; } = LayoutSettings.DefaultFocusRailWeight;

    public static LayoutSettingsEntry FromDomain(LayoutSettings settings) => new()
    {
        SingleSessionLayout = settings.SingleSessionLayout,
        StackSessionsVertically = settings.StackSessionsVertically,
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
        SidebarWidth = settings.SidebarWidth,
        SidebarCollapsed = settings.SidebarCollapsed,
        FocusRailWeight = settings.FocusRailWeight,
    };

    public LayoutSettings ToDomain() => new()
    {
        SingleSessionLayout = SingleSessionLayout,
        StackSessionsVertically = StackSessionsVertically,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        SidebarWidth = SidebarWidth,
        SidebarCollapsed = SidebarCollapsed,
        FocusRailWeight = FocusRailWeight,
    };
}
