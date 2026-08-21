using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Cockpit.App.ViewModels;

// One offered choice on an AskUserQuestion block (AC-715): the label the agent gets back, with the
// description that explains it. Clicking routes back to the owning question, which is the only thing that
// knows whether picking this one clears its siblings.
public partial class AskUserQuestionOptionViewModel(string label, string description) : ViewModelBase
{
    public string Label { get; } = label;

    public string Description { get; } = description;

    public bool HasDescription => Description.Length > 0;

    // Set by the owning question when it takes ownership of this option.
    public Action? SelectRequested { get; set; }

    // Set by the owning question at construction (AC-955): a radio glyph for single-select, a checkbox glyph
    // for multi-select — the tick is the same idea, but the two shapes say "one" and "several" apart.
    public bool MultiSelect { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    // The card stops accepting clicks once the answers have gone out, so an answered question reads as a record
    // of what was chosen rather than as a control that still does something.
    [ObservableProperty]
    private bool _isSelectable = true;

    public MaterialIconKind IconKind => MultiSelect
        ? (IsSelected ? MaterialIconKind.CheckboxMarked : MaterialIconKind.CheckboxBlankOutline)
        : (IsSelected ? MaterialIconKind.RadioboxMarked : MaterialIconKind.RadioboxBlank);

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(IconKind));

    [RelayCommand]
    private void Select() => SelectRequested?.Invoke();
}
