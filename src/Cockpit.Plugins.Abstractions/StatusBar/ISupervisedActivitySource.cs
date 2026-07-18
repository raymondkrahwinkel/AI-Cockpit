namespace Cockpit.Plugins.Abstractions.StatusBar;

/// <summary>
/// A plugin's source of <see cref="SupervisedActivity"/> for the status bar (AC-82): a label for the counter
/// ("Port-forwards"), the current set, and a signal for when that set changes so the host re-renders. Registered
/// through <see cref="ICockpitHost.AddSupervisedActivityProvider"/>. The host shows the counter only while there is
/// at least one activity, and a click opens a panel with the details and a Kill button per item — the operator's to
/// press, never the agent's.
/// </summary>
public interface ISupervisedActivitySource
{
    /// <summary>The counter label, e.g. "Port-forwards".</summary>
    string Label { get; }

    /// <summary>The activities running right now — a fresh snapshot each call.</summary>
    IReadOnlyList<SupervisedActivity> Snapshot();

    /// <summary>Raised when the set of activities changed, so the host recomputes the counter and refreshes an open panel.</summary>
    event Action? Changed;
}
