using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels.Onboarding;

// One entry in the wizard's step bar (AC-509): its label, whether it is the step showing now, and whether it is
// skipped — struck through rather than removed, so a skip is something the operator can see happened.
// `notBuilt` is a different reason to be dim: a slot the epic has planned (the step bar always shows all of
// them) but that has no `IFirstRunWizardStep` registered yet, e.g. the Depot step until AC-540 lands. Dimmed like
// a skip, but never struck through — nobody decided to skip it, it simply is not there to land on.
public sealed partial class WizardStepBarItemViewModel(string title, bool isSkipped, bool notBuilt = false) : ObservableObject
{
    public string Title { get; } = title;

    public bool IsSkipped { get; } = isSkipped;

    public bool NotBuiltYet { get; } = notBuilt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontWeight))]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    private bool _isCurrent;

    public FontWeight FontWeight => IsCurrent ? FontWeight.Bold : FontWeight.Normal;

    public TextDecorationCollection? TextDecorations => IsSkipped ? Avalonia.Media.TextDecorations.Strikethrough : null;

    public double Opacity => IsSkipped || NotBuiltYet ? 0.5 : IsCurrent ? 1.0 : 0.75;
}
