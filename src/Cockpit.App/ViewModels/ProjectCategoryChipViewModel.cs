using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One clickable chip under the project editor's Category field (AC-618): a category already in use somewhere, so
// the operator can pick it instead of retyping it. Clicking one is exactly the same as typing that text —
// `ProjectDialogViewModel.SelectCategoryCommand` only ever assigns `ProjectDialogViewModel.Category`,
// there is no second, chip-only path onto the saved project.
public partial class ProjectCategoryChipViewModel : ViewModelBase
{
    public string Name { get; }

    // Whether this chip names the category currently in the field — compared `System.StringComparison.OrdinalIgnoreCase`, the same as every other category comparison in this codebase (AC-372).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private bool _isActive;

    // The chip's own label, with a checkmark appended while it names the field's current value.
    public string DisplayLabel => IsActive ? $"{Name} ✓" : Name;

    public ProjectCategoryChipViewModel(string name, bool isActive)
    {
        Name = name;
        _isActive = isActive;
    }
}
