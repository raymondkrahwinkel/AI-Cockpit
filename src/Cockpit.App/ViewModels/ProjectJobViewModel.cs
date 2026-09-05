using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// AC-491: one editable job in the project editor — what it does, and the line saying what it changes. An
// untouched row is dropped on save; a half-filled one is refused there (see `ProjectDialogViewModel.SaveAsync`),
// since a job that cannot say what it touches is the one thing this list must never offer.
public partial class ProjectJobViewModel(string prompt = "", string blastRadius = "") : ViewModelBase
{
    [ObservableProperty]
    private string _prompt = prompt;

    [ObservableProperty]
    private string _blastRadius = blastRadius;

    public ProjectJob ToDomain() => new(Prompt.Trim(), BlastRadius.Trim());
}
