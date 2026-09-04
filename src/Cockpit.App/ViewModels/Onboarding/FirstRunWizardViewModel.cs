using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.ViewModels.Onboarding;

// One of the epic's fixed step-bar slots (AC-509's sharpened criteria, 2026-08-01): an `Order` a step registers
// under to claim it, and the label to show while nothing has claimed it yet.
public sealed record WizardPlannedSlot(int Order, string Title);

// Drives the first-run wizard shell (AC-509): the step bar, and Back/Next/Skip across whatever steps are handed
// in. A step whose `IFirstRunWizardStep.IsSkipped` is true stays in the bar, struck through, but
// Back/Next step over it — shown, not silently dropped.
public sealed partial class FirstRunWizardViewModel : ObservableObject
{
    // The epic's own plan (AC-508's four subs), so the step bar reads "Step 1 of 4" from the first screen even before
    // the Depot step (AC-540) exists to fill its slot — a slot nothing has registered for yet is shown dim rather than
    // left out, the same honesty AC-510's "Found ≠ works" already applies to a provider.
    public static readonly IReadOnlyList<WizardPlannedSlot> EpicPlan =
    [
        new(0, "What this is"),
        new(10, "Your account"),
        new(20, "What you have"),
        new(30, "What you work on"),
    ];

    private readonly IReadOnlyList<IFirstRunWizardStep> _steps;
    private readonly IReadOnlyList<Control> _stepContents;
    private readonly IReadOnlyList<int> _navigableIndexes;
    private readonly IReadOnlyList<int> _stepBarIndexByOrderedIndex;
    private readonly IReadOnlyList<int> _stepNumberByOrderedIndex;
    private readonly int _totalStepCount;
    private int _position;

    public FirstRunWizardViewModel(IReadOnlyList<IFirstRunWizardStep> steps, IReadOnlyList<WizardPlannedSlot>? plannedSlots = null)
    {
        if (steps.Count == 0)
        {
            throw new ArgumentException("The first-run wizard needs at least one registered step.", nameof(steps));
        }

        var ordered = steps.OrderBy(step => step.Order).ToList();

        // An Order is a slot and only one step can hold it: the slot match below takes the first step with that
        // Order, so a second one gets no bar item and keeps step number 0 — "Step 0 of 4", which compiles, passes
        // and reads as nothing. Fail where it is caused rather than on the screen (AC-508).
        var contestedSlot = ordered.GroupBy(step => step.Order).FirstOrDefault(slot => slot.Count() > 1);
        if (contestedSlot is not null)
        {
            var claimants = string.Join(", ", contestedSlot.Select(step => step.GetType().Name));
            throw new ArgumentException(
                $"First-run wizard steps {claimants} both claim Order {contestedSlot.Key}; an Order holds one step.",
                nameof(steps));
        }

        _steps = ordered;
        _stepContents = [.. ordered.Select(step => step.BuildContent())];

        if (plannedSlots is { Count: > 0 })
        {
            var slots = plannedSlots.OrderBy(slot => slot.Order).ToList();
            var stepBarIndexByOrderedIndex = new int[ordered.Count];
            var stepNumberByOrderedIndex = new int[ordered.Count];

            for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
            {
                var orderedIndex = ordered.FindIndex(step => step.Order == slots[slotIndex].Order);
                if (orderedIndex >= 0)
                {
                    StepBar.Add(new WizardStepBarItemViewModel(ordered[orderedIndex].Title, ordered[orderedIndex].IsSkipped));
                    stepBarIndexByOrderedIndex[orderedIndex] = slotIndex;
                    stepNumberByOrderedIndex[orderedIndex] = slotIndex + 1;
                }
                else
                {
                    StepBar.Add(new WizardStepBarItemViewModel(slots[slotIndex].Title, isSkipped: false, notBuilt: true));
                }
            }

            _stepBarIndexByOrderedIndex = stepBarIndexByOrderedIndex;
            _stepNumberByOrderedIndex = stepNumberByOrderedIndex;
            _totalStepCount = slots.Count;
        }
        else
        {
            for (var index = 0; index < ordered.Count; index++)
            {
                StepBar.Add(new WizardStepBarItemViewModel(ordered[index].Title, ordered[index].IsSkipped));
            }

            _stepBarIndexByOrderedIndex = [.. Enumerable.Range(0, ordered.Count)];
            _stepNumberByOrderedIndex = [.. Enumerable.Range(1, ordered.Count)];
            _totalStepCount = ordered.Count;
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

    [ObservableProperty]
    private string _stepProgressLabel = "";

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

    // What the startup route does when the wizard window closes: mark the wizard done, then show the cockpit.
    // A step that restored a backup (AC-1280) has already done the first and asked for a restart, so both are
    // skipped — a cockpit started here would come up on the settings that restore had just replaced.
    internal void FinishFromStartup(IFirstRunWizardStateStore stateStore, Action startCockpit)
    {
        if (_steps.OfType<RestoreStep>().Any(step => step.ViewModel.TookOver))
        {
            return;
        }

        _ = stateStore.MarkCompletedAsync(FirstRunWizardVersion.Current);
        startCockpit();
    }

    private void _ApplyPosition()
    {
        var stepIndex = _navigableIndexes[_position];
        CurrentStepContent = _stepContents[stepIndex];
        CanGoBack = _position > 0;
        IsLastStep = _position == _navigableIndexes.Count - 1;
        StepProgressLabel = $"Step {_stepNumberByOrderedIndex[stepIndex]} of {_totalStepCount}";

        for (var index = 0; index < StepBar.Count; index++)
        {
            StepBar[index].IsCurrent = false;
        }

        StepBar[_stepBarIndexByOrderedIndex[stepIndex]].IsCurrent = true;
    }
}
