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
    // The epic's own plan (AC-508's subs — AC-585 added slot 25), so the step bar reads "Step 1 of 5" even before
    // a slot's step exists (AC-540's Depot step) — shown dim rather than left out, the honesty AC-510 applies too.
    public static readonly IReadOnlyList<WizardPlannedSlot> EpicPlan =
    [
        new(0, "What this is"),
        new(10, "Getting started"),
        new(20, "What you have"),
        new(25, "Your assistant"),
        new(30, "What you work on"),
    ];

    private readonly IReadOnlyList<IFirstRunWizardStep> _steps;
    private readonly IReadOnlyList<Control> _stepContents;
    private readonly IReadOnlyList<WizardStepBarItemViewModel?> _barItemByOrderedIndex;
    private int _current;

    public FirstRunWizardViewModel(IReadOnlyList<IFirstRunWizardStep> steps, IReadOnlyList<WizardPlannedSlot>? plannedSlots = null, int? startAtOrder = null)
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

        var barItemByOrderedIndex = new WizardStepBarItemViewModel?[ordered.Count];

        if (plannedSlots is { Count: > 0 })
        {
            foreach (var slot in plannedSlots.OrderBy(slot => slot.Order))
            {
                var orderedIndex = ordered.FindIndex(step => step.Order == slot.Order);
                if (orderedIndex >= 0)
                {
                    var barItem = new WizardStepBarItemViewModel(ordered[orderedIndex].Title, ordered[orderedIndex].IsSkipped);
                    barItemByOrderedIndex[orderedIndex] = barItem;
                    StepBar.Add(barItem);
                }
                else
                {
                    StepBar.Add(new WizardStepBarItemViewModel(slot.Title, isSkipped: false, notBuilt: true));
                }
            }
        }
        else
        {
            for (var index = 0; index < ordered.Count; index++)
            {
                var barItem = new WizardStepBarItemViewModel(ordered[index].Title, ordered[index].IsSkipped);
                barItemByOrderedIndex[index] = barItem;
                StepBar.Add(barItem);
            }
        }

        _barItemByOrderedIndex = barItemByOrderedIndex;

        // The choice step settles later questions while the wizard is open (AC-1504), so the skips are read again
        // whenever its answers change rather than once here.
        if (ordered.OfType<StartChoiceStep>().FirstOrDefault() is { } choice)
        {
            choice.ViewModel.AnswersChanged += (_, _) => RefreshSkips();
        }

        var navigable = _Navigable();
        _current = navigable.FirstOrDefault(index => ordered[index].Order == startAtOrder, navigable[0]);
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

    // Why Next is off on this step; empty when it is on.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string _nextBlockedReason = "";

    [ObservableProperty]
    private string _stepProgressLabel = "";

    // Raised when the operator is done with the wizard — Skip, or Next on the last step. The window closes on this.
    public event EventHandler? RequestClose;

    private bool CanGoNext() => NextBlockedReason.Length == 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        var navigable = _Navigable();
        _current = navigable[navigable.IndexOf(_current) - 1];
        _ApplyPosition();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (IsLastStep)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);

            return;
        }

        var navigable = _Navigable();
        _current = navigable[navigable.IndexOf(_current) + 1];
        _ApplyPosition();
    }

    // AC-509 criterion 3: Skip leaves everything uninstalled — it does nothing here beyond closing the window.
    // FirstRunWizardService is what marks the wizard complete once it closes, the same way it does for Next on
    // the last step, so neither path has its own copy of that bookkeeping.
    [RelayCommand]
    private void Skip() => RequestClose?.Invoke(this, EventArgs.Empty);

    // What the startup route does when the wizard window closes: await marking the wizard done (AC-1336: not
    // fire-and-forget, so closing rounds up the write), then show the cockpit. A step that restored a backup
    // (AC-1280) has already done the first and asked for a restart, so both are skipped here.
    internal async Task FinishFromStartupAsync(IFirstRunWizardStateStore stateStore, Action startCockpit)
    {
        if (_steps.OfType<StartChoiceStep>().Any(step => step.RestoreViewModel.TookOver))
        {
            return;
        }

        await stateStore.MarkCompletedAsync(FirstRunWizardVersion.Current);
        startCockpit();
    }

    // Reads every step's IsSkipped again: the bar strikes it through or not, and "Step n of N" counts accordingly.
    internal void RefreshSkips() => _ApplyPosition();

    // Skipped steps stay visible in the bar but are never landed on — Back/Next only ever stop on one of these
    // indexes. If every step happens to be skipped there is nothing left to walk, so the wizard shows the first one
    // anyway rather than throwing on a state the operator did nothing to cause.
    private List<int> _Navigable()
    {
        var navigable = Enumerable.Range(0, _steps.Count).Where(index => !_steps[index].IsSkipped).ToList();

        return navigable.Count == 0 ? [0] : navigable;
    }

    private void _ApplyPosition()
    {
        for (var index = 0; index < _steps.Count; index++)
        {
            if (_barItemByOrderedIndex[index] is { } barItem)
            {
                barItem.IsSkipped = _steps[index].IsSkipped;
                barItem.IsCurrent = index == _current;
            }
        }

        var navigable = _Navigable();
        var position = navigable.IndexOf(_current);
        CurrentStepContent = _stepContents[_current];
        CanGoBack = position > 0;
        NextBlockedReason = _steps[_current].NextBlockedReason ?? "";
        IsLastStep = position == navigable.Count - 1;

        // Skipped steps are not steps: they are left out of both numbers. A planned slot nothing is registered for
        // still counts, because it is dim, not skipped.
        var counted = StepBar.Where(item => !item.IsSkipped).ToList();
        var number = _barItemByOrderedIndex[_current] is { } current ? counted.IndexOf(current) + 1 : 0;
        StepProgressLabel = $"Step {number} of {counted.Count}";
    }
}
