using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// One project as the Projects workspace list shows it (AC-618): the project itself plus a one-line origin badge —
// "● This machine" or "◆ &lt;connection&gt;" — that replaces AC-245's separate "On this machine" heading over
// every bound project. See `ProjectsViewModel`'s own remarks on where `OriginBadge` comes
// from. An ObservableObject rather than a record (AC-709): `IsSelected` needs to change in place and notify the
// card's own Border, the same reason `SessionPanelViewModel.IsSelected` isn't a plain field either.
public sealed partial class ProjectCardViewModel(Project project, string originBadge) : ObservableObject
{
    public Project Project { get; } = project;

    public string OriginBadge { get; } = originBadge;

    // AC-620: whether the launcher's own pill badge should show at all — never for "● This machine" (a local
    // project says nothing a launcher card needs to add), only for a bound one.
    public bool IsShared => OriginBadge.StartsWith('◆');

    // Kept in sync with `ProjectsViewModel.SelectedProject` by that view model (construction time in
    // `_ToCard`, live updates in `OnSelectedProjectChanged`) so the card's Border can bind its selected style to it.
    [ObservableProperty]
    private bool _isSelected;
}
