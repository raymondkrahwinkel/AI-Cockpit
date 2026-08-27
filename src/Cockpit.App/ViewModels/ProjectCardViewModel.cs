using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// An ObservableObject rather than a record (AC-709): `IsSelected` needs to change in place and notify the card's own
// Border, the same reason `SessionPanelViewModel.IsSelected` isn't a plain field either (AC-618, AC-245).
public sealed partial class ProjectCardViewModel(
    Project project,
    string originBadge,
    ProjectCardActions? actions = null,
    bool hasRemoteChanges = false) : ObservableObject
{
    public Project Project { get; } = project;

    public string OriginBadge { get; } = originBadge;

    public ProjectCardActions? Actions { get; } = actions;

    // AC-894: `DepotSyncWatcher` most recently found a checksum on the Depot side that moved since this project was
    // last checked — a visible nudge only, never an automatic overwrite of an unsaved local edit.
    public bool HasRemoteChanges { get; } = hasRemoteChanges;

    // AC-620: whether the launcher's own pill badge should show at all — never for "● This machine" (a local
    // project says nothing a launcher card needs to add), only for a bound one.
    public bool IsShared => OriginBadge.StartsWith('◆');

    // Whether the project names a profile that still exists, and so has something to run on. False is not a fault —
    // it is a project that was created and not finished — which is why the card asks it to be finished rather than
    // reporting it (AC-772 criterion 12).
    public bool CanStart => !string.IsNullOrEmpty(Project.DefaultProfileLabel);

    // AC-772: one button, two directions, the same either/or `ShareProjectCommand` answers.
    public string ShareLabel => IsShared ? "Stop sharing…" : "Share with your team…";

    public string ShareTooltip => IsShared
        ? "Remove this project's connection on this machine — nothing shared elsewhere is affected"
        : "Publish this project so colleagues can work from the same definition";

    // Kept in sync with `ProjectsViewModel.SelectedProject` by that view model (construction time in
    // `_ToCard`, live updates in `OnSelectedProjectChanged`) so the card's Border can bind its selected style to it.
    [ObservableProperty]
    private bool _isSelected;
}
