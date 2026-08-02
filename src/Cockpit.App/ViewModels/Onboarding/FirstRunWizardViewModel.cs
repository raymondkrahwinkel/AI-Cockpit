using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Views.Onboarding;

namespace Cockpit.App.ViewModels.Onboarding;

// Drives the first-run wizard shell (AC-509): the step bar, and Back/Next/Skip across whatever steps are handed
// in. A step whose `IFirstRunWizardStep.IsSkipped` is true stays in the bar, struck through, but
// Back/Next step over it — shown, not silently dropped, the same way a step that was never registered at all
// (the Depot step, AC-540) is silently absent rather than shown broken.
public sealed partial class FirstRunWizardViewModel : ObservableObject
{
    private readonly IReadOnlyList<Control> _stepContents;
    private readonly IReadOnlyList<int> _navigableIndexes;
    private int _position;

    public FirstRunWizardViewModel(IReadOnlyList<IFirstRunWizardStep> steps)
    {
        if (steps.Count == 0)
        {
            throw new ArgumentException("The first-run wizard needs at least one registered step.", nameof(steps));
        }

        var ordered = steps.OrderBy(step => step.Order).ToList();
        _stepContents = [.. ordered.Select(step => step.BuildContent())];

        foreach (var step in ordered)
        {
            StepBar.Add(new WizardStepBarItemViewModel(step.Title, step.IsSkipped));
        }

        // Skipped steps stay visible in the bar above but are never landed on — Back/Next only ever stop on one
        // of these indexes. If every step happens to be skipped there is nothing left to walk, so the wizard shows
        // the first one anyway rather than throwing on a state the operator did nothing to cause.
        _navigableIndexes = [.. Enumerable.Range(0, ordered.Count).Where(index => !ordered[index].IsSkipped)];
        if (_navigableIndexes.Count == 0)
        {
            _navigableIndexes = [0];
        }

        _ApplyPosition();
    }

    // Design-time constructor for the previewer.
    public FirstRunWizardViewModel()
        : this([new WelcomeStep()])
    {
    }

    public ObservableCollection<WizardStepBarItemViewModel> StepBar { get; } = [];

    [ObservableProperty]
    private Control? _currentStepContent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _isLastStep;

    // Raised when the operator is done with the wizard — Skip, or Next on the last step. The window closes on this.
    public event EventHandler? RequestClose;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        _position--;
        _ApplyPosition();
    }

    [RelayCommand]
    private void Next()
    {
        if (IsLastStep)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);

            return;
        }

        _position++;
        _ApplyPosition();
    }

    // AC-509 criterion 3: Skip leaves everything uninstalled — it does nothing here beyond closing the window.
    // FirstRunWizardService is what marks the wizard complete once it closes, the same way it does for Next on
    // the last step, so neither path has its own copy of that bookkeeping.
    [RelayCommand]
    private void Skip() => RequestClose?.Invoke(this, EventArgs.Empty);

    private void _ApplyPosition()
    {
        var stepIndex = _navigableIndexes[_position];
        CurrentStepContent = _stepContents[stepIndex];
        CanGoBack = _position > 0;
        IsLastStep = _position == _navigableIndexes.Count - 1;

        for (var index = 0; index < StepBar.Count; index++)
        {
            StepBar[index].IsCurrent = index == stepIndex;
        }
    }
}
