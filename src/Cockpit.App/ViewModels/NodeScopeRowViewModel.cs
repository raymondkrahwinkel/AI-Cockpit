using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One profile or project on the AC-794 scope checklist: what the operator ticks to let the current pairing use it.
// `Key` is what `NodePairingBroker.SetScopeAsync` actually stores (a profile label or a project id); `Label` is
// only for display, so a renamed project does not need this row rebuilt to still read right.
public sealed partial class NodeScopeRowViewModel(string key, string label) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    // Unchecked by construction — every row starts this way, which is what makes "empty by default" (criterion 2)
    // a property of how rows are built rather than something `SecurityOptionsViewModel` has to remember to set.
    [ObservableProperty]
    private bool _isAllowed;
}
