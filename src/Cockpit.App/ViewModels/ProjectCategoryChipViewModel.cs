using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One clickable chip under the project editor's Category field (AC-618): a category already in use somewhere, so
/// the operator can pick it instead of retyping it. Clicking one is exactly the same as typing that text —
/// <see cref="ProjectDialogViewModel.SelectCategoryCommand"/> only ever assigns <see cref="ProjectDialogViewModel.Category"/>,
/// there is no second, chip-only path onto the saved project.
/// </summary>
public partial class ProjectCategoryChipViewModel : ViewModelBase
{
    public string Name { get; }

    /// <summary>Whether this chip names the category currently in the field — compared <see cref="System.StringComparison.OrdinalIgnoreCase"/>, the same as every other category comparison in this codebase (AC-372).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private bool _isActive;

    /// <summary>The chip's own label, with a checkmark appended while it names the field's current value.</summary>
    public string DisplayLabel => IsActive ? $"{Name} ✓" : Name;

    public ProjectCategoryChipViewModel(string name, bool isActive)
    {
        Name = name;
        _isActive = isActive;
    }
}
