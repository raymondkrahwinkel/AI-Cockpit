using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;

namespace Cockpit.App.Docking;

// Host-internal registry for the right-hand dock rail (AC-951). `DockPanelRegistration` lives in
// `Cockpit.Plugins.Abstractions.Docking` (AC-960, plugin-facing via `ICockpitHost.AddDockPanel`), but this
// registry itself stays a standalone type here — deliberately not derived from `IWidgetRegistry`.

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
