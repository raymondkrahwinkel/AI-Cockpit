using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// AC-1306: one node of the panels sidebar's session tree. This tree groups WHERE A SESSION STANDS — a Sessions
// workspace and the sessions `SessionWorkspacePlacement.Resolve` places on it. AC-1302's Simple-stand rail groups
// WHO DRIVES WHAT. Same shape, different meaning; folding them into one tree would make half of it untrue.

// Reconciled in place rather than rebuilt (`CockpitViewModel._SyncSessionWorkspaceGroups`), so a context menu open
// on one row survives a change to a node it does not own — the rule `VisibleSessions` follows for AC-561.
public sealed partial class SessionWorkspaceGroupViewModel : ObservableObject
{
    public SessionWorkspaceGroupViewModel(string id)
    {
        Id = id;
        Sessions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CountLabel));
            OnPropertyChanged(nameof(ShowEmptyNote));
        };
    }

    public string Id { get; }

    // The sessions on this workspace, in the sidebar's own order.
    public ObservableCollection<SessionPanelViewModel> Sessions { get; } = [];

    // AC-487: the operator's word for this is a workspace tab, so the node carries the tab's own name.
    [ObservableProperty]
    private string _name = string.Empty;

    // True for the workspace now showing — the node the tab strip and this tree both point at.
    [ObservableProperty]
    private bool _isActive;

    // False while the chevron holds this node closed. Persisted in `WorkspaceSettings.CollapsedSidebarWorkspaceIds`.
    [ObservableProperty]
    private bool _isExpanded = true;

    // How many sessions stand here, beside the name.
    public string CountLabel => Sessions.Count.ToString();

    // Criterion 4(b): the active workspace keeps its node even with nothing on it, and says so rather than
    // leaving a name with a gap under it. Only worth drawing while the node is open.
    public bool ShowEmptyNote => Sessions.Count == 0 && IsExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyNote));
}
