using Avalonia.Controls;
using Material.Icons;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Docking;

// A panel the right-hand dock rail (AC-951) can show — the dock equivalent of a
// `Cockpit.Plugins.Abstractions.Widgets.WidgetRegistration`, but host-internal rather than
// plugin-facing: there is exactly one candidate content today (the Assistant, AC-950 [c]), so this stays a
// seam inside `Cockpit.App` instead of a surface on `ICockpitHost` that could never be withdrawn.
//
// `Id`: A stable id, persisted as `LayoutSettings.OpenDockPanelId` so the open panel survives a restart.
// `Title`: Shown as the vertical tab label on the collapsed rail.
// `IconKind`: Shown above the title on the rail tab.
// `CreateView`: Builds the panel's content, on the UI thread, once per time it is opened.
public sealed record DockPanelRegistration(string Id, string Title, MaterialIconKind IconKind, Func<Control> CreateView);

/// <summary>Holds the panels the dock rail offers — the Assistant since AC-953, registered by <c>AssistantIndicatorCoordinator</c>.</summary>
public interface IDockPanelRegistry
{
    /// <returns>False when another registration already claims this id — first one wins.</returns>
    bool Register(DockPanelRegistration panel);

    /// <summary>Withdraws a panel the rail can no longer show — an undocked Assistant lives in its own window, and a tab for it there would open a second one.</summary>
    /// <returns>False when no panel of that id was registered.</returns>
    bool Unregister(string id);

    /// <summary>Every panel registered so far, in registration order — what the rail's tab strip lists.</summary>
    IReadOnlyList<DockPanelRegistration> Panels { get; }

    event EventHandler? Changed;
}

internal sealed class DockPanelRegistry : IDockPanelRegistry, ISingletonService
{
    private readonly List<DockPanelRegistration> _panels = [];

    public event EventHandler? Changed;

    public IReadOnlyList<DockPanelRegistration> Panels => [.. _panels];

    public bool Register(DockPanelRegistration panel)
    {
        if (_panels.Any(existing => existing.Id == panel.Id))
        {
            return false;
        }

        _panels.Add(panel);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Unregister(string id)
    {
        if (_panels.RemoveAll(panel => panel.Id == id) == 0)
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
