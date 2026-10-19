using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace Cockpit.App.ViewModels.Onboarding;

// One way to begin on the wizard's choice step (AC-1504): the host's own two, or a route a plugin offers. The content
// is built the first time the option is picked and kept, so going back and forth does not rebuild (or reset) it.
public sealed partial class StartChoiceOptionViewModel(
    string id,
    MaterialIconKind iconKind,
    string title,
    string description,
    Func<Control>? createContent,
    string offeredBy = "",
    bool answersWorkKind = false) : ObservableObject
{
    private Control? _content;

    public string Id { get; } = id;

    public MaterialIconKind IconKind { get; } = iconKind;

    public string Title { get; } = title;

    public string Description { get; } = description;

    // The plugin's display name, empty for the host's own choices.
    public string OfferedBy { get; } = offeredBy;

    public string OfferedByLabel => OfferedBy.Length == 0 ? string.Empty : $"Offered by the {OfferedBy} plugin";

    public bool AnswersWorkKind { get; } = answersWorkKind;

    [ObservableProperty]
    private bool _isSelected;

    public Control? Content => _content ??= createContent?.Invoke();
}
