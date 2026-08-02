using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels.Onboarding;

/// <summary>
/// One entry in the wizard's step bar (AC-509): its label, whether it is the step showing now, and whether it is
/// skipped — struck through rather than removed, so a skip is something the operator can see happened.
/// </summary>
public sealed partial class WizardStepBarItemViewModel(string title, bool isSkipped) : ObservableObject
{
    public string Title { get; } = title;

    public bool IsSkipped { get; } = isSkipped;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontWeight))]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    private bool _isCurrent;

    public FontWeight FontWeight => IsCurrent ? FontWeight.Bold : FontWeight.Normal;

    public TextDecorationCollection? TextDecorations => IsSkipped ? Avalonia.Media.TextDecorations.Strikethrough : null;

    public double Opacity => IsSkipped ? 0.5 : IsCurrent ? 1.0 : 0.75;
}
