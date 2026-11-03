using NSubstitute;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;

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

    // AC-1336: MarkCompletedAsync was started fire-and-forget, so a slow write could lose the completion marker
    // to the app closing around it. Awaited, closing now waits — a state store that only completes after being
    // told to proves it: startCockpit must not run before that resolves.
    [Fact]
    public Task FinishFromStartupAsync_WaitsForMarkCompletedAsync_BeforeStartingTheCockpit() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var viewModel = new FirstRunWizardViewModel([new StubFirstRunWizardStep(0, "What this is", isSkipped: false)]);
            var markCompleted = new TaskCompletionSource();
            var stateStore = Substitute.For<IFirstRunWizardStateStore>();
            stateStore.MarkCompletedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(markCompleted.Task);
            var cockpitStarted = false;

            var finish = viewModel.FinishFromStartupAsync(stateStore, () => cockpitStarted = true);

            Assert.False(cockpitStarted);

            markCompleted.SetResult();
            await finish;

            Assert.True(cockpitStarted);
        });
}
