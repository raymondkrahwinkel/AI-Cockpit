using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views.Onboarding;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// The wizard shell's own navigation (AC-509), independent of any real step's content: a step that is registered
/// but marked <see cref="IFirstRunWizardStep.IsSkipped"/> stays in the step bar, struck through, while Back/Next
/// step over it — and a step that was never registered at all (the shape AC-540's Depot step will take, since it
/// is not built) is simply absent from the bar, nothing here has to know it could have existed.
/// </summary>
/// <remarks>
/// Every body runs through <see cref="HeadlessAvalonia.Run(System.Action)"/>: the view model builds
/// <c>Border</c> content and hands out <c>FontWeight</c>/<c>TextDecorationCollection</c> values, and touching
/// Avalonia types off the dispatcher thread xunit happens to hand a test corrupts shared platform state rather
/// than throwing where the mistake was made — it surfaced as sibling test classes failing later in the same run,
/// not as a failure here.
/// </remarks>
[Collection("avalonia")]
public class FirstRunWizardViewModelTests
{
    [Fact]
    public void Constructor_OnlyListsTheStepsHandedIn() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new FirstRunWizardViewModel([new StubFirstRunWizardStep(0, "What this is", isSkipped: false)]);

        Assert.Single(viewModel.StepBar);
        Assert.Equal("What this is", viewModel.StepBar[0].Title);
    });

    [Fact]
    public void Constructor_StartsOnTheFirstStep_CurrentAndNotGoingBack() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new FirstRunWizardViewModel(
        [
            new StubFirstRunWizardStep(0, "What this is", isSkipped: false),
            new StubFirstRunWizardStep(1, "Your account", isSkipped: false),
        ]);

        Assert.True(viewModel.StepBar[0].IsCurrent);
        Assert.False(viewModel.StepBar[1].IsCurrent);
        Assert.False(viewModel.CanGoBack);
        Assert.False(viewModel.IsLastStep);
    });

    [Fact]
    public void Next_AdvancesToTheNextStep_AndMovesTheCurrentMarker() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new FirstRunWizardViewModel(
        [
            new StubFirstRunWizardStep(0, "What this is", isSkipped: false),
            new StubFirstRunWizardStep(1, "Your account", isSkipped: false),
        ]);
        var secondStepContent = viewModel.CurrentStepContent;

        viewModel.NextCommand.Execute(null);

        Assert.False(viewModel.StepBar[0].IsCurrent);
        Assert.True(viewModel.StepBar[1].IsCurrent);
        Assert.True(viewModel.CanGoBack);
        Assert.True(viewModel.IsLastStep);
        Assert.NotSame(secondStepContent, viewModel.CurrentStepContent);
    });

    [Fact]
    public void NextThenBack_SkipsOverAStepMarkedSkipped_BothWays() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new FirstRunWizardViewModel(
        [
            new StubFirstRunWizardStep(0, "What this is", isSkipped: false),
            new StubFirstRunWizardStep(1, "What you have", isSkipped: true), // the Depot step, AC-511-style skip
            new StubFirstRunWizardStep(2, "What you work on", isSkipped: false),
        ]);

        viewModel.NextCommand.Execute(null);
        Assert.True(viewModel.StepBar[2].IsCurrent, "Next stepped over the skipped middle step");
        Assert.False(viewModel.StepBar[1].IsCurrent);
        Assert.True(viewModel.IsLastStep);

        viewModel.BackCommand.Execute(null);
        Assert.True(viewModel.StepBar[0].IsCurrent, "Back stepped over the same skipped step going the other way");
        Assert.False(viewModel.CanGoBack);
    });

    [Fact]
    public void SkippedStep_StaysInTheBarStruckThrough_RatherThanBeingRemoved() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new FirstRunWizardViewModel(
        [
            new StubFirstRunWizardStep(0, "What this is", isSkipped: false),
            new StubFirstRunWizardStep(1, "What you have", isSkipped: true),
        ]);

        Assert.Equal(2, viewModel.StepBar.Count);
        Assert.True(viewModel.StepBar[1].IsSkipped);
        Assert.NotNull(viewModel.StepBar[1].TextDecorations);
    });

    [Fact]
    public void Skip_RaisesRequestClose_WithoutMoving() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new FirstRunWizardViewModel([new StubFirstRunWizardStep(0, "What this is", isSkipped: false)]);
        var raised = false;
        viewModel.RequestClose += (_, _) => raised = true;

        viewModel.SkipCommand.Execute(null);

        Assert.True(raised);
    });

    [Fact]
    public void Next_OnTheLastStep_RaisesRequestCloseInsteadOfMovingPastIt() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new FirstRunWizardViewModel([new StubFirstRunWizardStep(0, "What this is", isSkipped: false)]);
        var raised = false;
        viewModel.RequestClose += (_, _) => raised = true;

        viewModel.NextCommand.Execute(null);

        Assert.True(raised);
    });
}
