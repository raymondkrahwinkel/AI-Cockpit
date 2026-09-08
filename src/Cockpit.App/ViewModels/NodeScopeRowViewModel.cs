using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One profile or project on the AC-794 scope checklist: what the operator ticks to let the current pairing use it.
// `Key` is what `NodePairingBroker.SetScopeAsync` actually stores (a profile label or a project id); `Label` is
// only for display, so a renamed project does not need this row rebuilt to still read right.
public sealed partial class NodeScopeRowViewModel(string key, string label, bool skipsApprovals = false) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    // AC-1292: set for a profile row whose profile starts past its own approval gate, so the page where the reach
    // is given away says what is being given away. Never set for a project row — a project has no such stand.
    public bool SkipsApprovals { get; } = skipsApprovals;

    // Unchecked by construction — every row starts this way, which is what makes "empty by default" (criterion 2)
    // a property of how rows are built rather than something `SecurityOptionsViewModel` has to remember to set.
    [ObservableProperty]
    private bool _isAllowed;
}
