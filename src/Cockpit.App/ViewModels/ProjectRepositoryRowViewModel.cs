using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// One extra repository row in the project editor (AC-938). The existing "Folder" row stays repo #1
// (ProjectDialogViewModel.SourceDirectory); a row here is repo #2 and on, in the same ItemsControl +
// Add-command idiom ResourceRows already uses — not a new field type.
public partial class ProjectRepositoryRowViewModel : ObservableObject
{
    public ProjectRepositoryRowViewModel(string path = "", string? label = null)
    {
        _path = path;
        _label = label ?? string.Empty;
    }

    [ObservableProperty]
    private string _path;

    // What the operator calls this repository ("web", "android") — blank is fine, every reader falls back to the
    // folder's own name.
    [ObservableProperty]
    private string _label;

    public ProjectRepository ToDomain() => new(Path.Trim())
    {
        Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
    };
}
