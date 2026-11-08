using Cockpit.Core.Layout;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `LayoutSettings` in the `layout` section of `cockpit.json`.
internal sealed class LayoutSettingsEntry
{
    public AgentsOverviewFilters AgentsOverviewFilters { get; set; } = new();
    public bool SingleSessionLayout { get; set; }

    public bool StackSessionsVertically { get; set; }

    public bool FocusRailLayout { get; set; }

    public bool MinimizeToTrayOnClose { get; set; }

    public double SidebarWidth { get; set; } = LayoutSettings.DefaultSidebarWidth;

    public bool SidebarCollapsed { get; set; }

    public bool WaitingFilterOn { get; set; }

    public double FocusRailWeight { get; set; } = LayoutSettings.DefaultFocusRailWeight;

    public double DockRailWidth { get; set; } = LayoutSettings.DefaultDockRailWidth;

    public string? OpenDockPanelId { get; set; }

    public bool AssistantDocked { get; set; }

    public List<string>? FloatingDockPanelIds { get; set; }

    public bool CompanionWindowVisible { get; set; }

    public CompanionOrientation CompanionOrientation { get; set; } = CompanionOrientation.Horizontal;

    public bool CompanionAlwaysOnTop { get; set; } = true;

    public CompanionStripLayout? CompanionStripLayout { get; set; }

    public bool OpenInSimpleView { get; set; }

    public bool AgentsPaletteEnabled { get; set; } = true;

    public bool AgentsPaletteRailIcon { get; set; }

    public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;

    // AC-1748: these two were never here, so the thread rail's "Icons only" was dropped on every save.
    public AssistantThreadRail AssistantThreadRailWindow { get; set; }

    public AssistantThreadRail AssistantThreadRailDock { get; set; }

    public List<string>? CollapsedSidebarSections { get; set; }

    public static LayoutSettingsEntry FromDomain(LayoutSettings settings) => new()
    {
        AgentsOverviewFilters = settings.AgentsOverviewFilters,
        SingleSessionLayout = settings.SingleSessionLayout,
        StackSessionsVertically = settings.StackSessionsVertically,
        FocusRailLayout = settings.FocusRailLayout,
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
        SidebarWidth = settings.SidebarWidth,
        SidebarCollapsed = settings.SidebarCollapsed,
        WaitingFilterOn = settings.WaitingFilterOn,
        FocusRailWeight = settings.FocusRailWeight,
        DockRailWidth = settings.DockRailWidth,
        OpenDockPanelId = settings.OpenDockPanelId,
        AssistantDocked = settings.AssistantDocked,
        FloatingDockPanelIds = settings.FloatingDockPanelIds?.ToList(),
        CompanionWindowVisible = settings.CompanionWindowVisible,
        CompanionOrientation = settings.CompanionOrientation,
        CompanionAlwaysOnTop = settings.CompanionAlwaysOnTop,
        CompanionStripLayout = settings.CompanionStripLayout,
        OpenInSimpleView = settings.OpenInSimpleView,
        AgentsPaletteEnabled = settings.AgentsPaletteEnabled,
        AgentsPaletteRailIcon = settings.AgentsPaletteRailIcon,
        ThemeMode = settings.ThemeMode,
        AssistantThreadRailWindow = settings.AssistantThreadRailWindow,
        AssistantThreadRailDock = settings.AssistantThreadRailDock,
        CollapsedSidebarSections = settings.CollapsedSidebarSections.ToList(),
    };

    public LayoutSettings ToDomain() => new()
    {
        AgentsOverviewFilters = AgentsOverviewFilters,
        SingleSessionLayout = SingleSessionLayout,
        StackSessionsVertically = StackSessionsVertically,
        FocusRailLayout = FocusRailLayout,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        SidebarWidth = SidebarWidth,
        SidebarCollapsed = SidebarCollapsed,
        WaitingFilterOn = WaitingFilterOn,
        FocusRailWeight = FocusRailWeight,
        DockRailWidth = DockRailWidth,
        OpenDockPanelId = OpenDockPanelId,
        AssistantDocked = AssistantDocked,
        FloatingDockPanelIds = FloatingDockPanelIds,
        CompanionWindowVisible = CompanionWindowVisible,
        CompanionOrientation = CompanionOrientation,
        CompanionAlwaysOnTop = CompanionAlwaysOnTop,
        CompanionStripLayout = CompanionStripLayout,
        OpenInSimpleView = OpenInSimpleView,
        AgentsPaletteEnabled = AgentsPaletteEnabled,
        AgentsPaletteRailIcon = AgentsPaletteRailIcon,
        ThemeMode = ThemeMode,
        AssistantThreadRailWindow = AssistantThreadRailWindow,
        AssistantThreadRailDock = AssistantThreadRailDock,
        CollapsedSidebarSections = CollapsedSidebarSections ?? [],
    };
}
