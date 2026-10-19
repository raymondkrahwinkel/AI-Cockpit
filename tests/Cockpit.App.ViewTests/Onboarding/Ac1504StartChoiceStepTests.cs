using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views.Onboarding;
using Cockpit.Plugins.Abstractions.UI.Onboarding;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// AC-1504: the choice step on slot 10 offers what is there to offer, and what the picked route answers is what the
/// step count follows.
/// </summary>
[Collection("avalonia")]
public sealed class Ac1504StartChoiceStepTests
{
    [Fact]
    public void TheChoices_FollowThePlugins_AndTheStepCount_FollowsTheRoute() => HeadlessAvalonia.Run(() =>
    {
        var registry = new FirstStartRouteRegistry();
        var restore = new RestoreStepViewModel();
        var choice = new StartChoiceStep(restore, registry);
        var wizard = new FirstRunWizardViewModel(
            [
                new WelcomeStep(),
                choice,
                new StubFirstRunWizardStep(20, "What you have", isSkipped: false),
                new StubFirstRunWizardStep(25, "Your assistant", isSkipped: false),
                new WorkKindStep(new WorkKindStepViewModel([]), choice),
            ],
            FirstRunWizardViewModel.EpicPlan);
        wizard.NextCommand.Execute(null);
        string[] Titles() => [.. choice.ViewModel.Options.Select(option => option.Title)];

        // Without a plugin there is nothing but the host's own two, and "Restore a backup file" is the restore page
        // of AC-1280 on the very view model the wizard finishes from, not a copy of it.
        Assert.Equal(["Start fresh", "Restore a backup file"], Titles());
        choice.ViewModel.ChooseCommand.Execute(choice.ViewModel.Options[1]);
        Assert.Same(restore, Assert.IsType<RestoreStepView>(choice.ViewModel.SelectedContent).DataContext);
        choice.ViewModel.ChooseCommand.Execute(choice.ViewModel.Options[0]);
        Assert.Equal("Step 2 of 5", wizard.StepProgressLabel);

        // A plugin that registers after the wizard is open still shows up as a third choice, named after its plugin.
        Assert.True(registry.Register(
            new FirstStartRoute("test.sign-in", "Sign in", "Take your setup along.", () => new Border()) { AnswersWorkKind = true }, "Test"));
        Assert.False(registry.Register(new FirstStartRoute("test.sign-in", "Again", "A second one.", () => new Border()), "Other"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Start fresh", "Restore a backup file", "Sign in"], Titles());
        Assert.Equal("Offered by the Test plugin", choice.ViewModel.Options[2].OfferedByLabel);

        // A route that answers the work-kind question strikes that step through and drops it from the count.
        choice.ViewModel.ChooseCommand.Execute(choice.ViewModel.Options[2]);
        Assert.True(wizard.StepBar[4].IsSkipped);
        Assert.Equal("Step 2 of 4", wizard.StepProgressLabel);
        wizard.NextCommand.Execute(null);
        wizard.NextCommand.Execute(null);
        Assert.Equal("Step 4 of 4", wizard.StepProgressLabel);
        Assert.True(wizard.IsLastStep);

        // Picking "Start fresh" again gives the step back, so the choice is not a one-way door.
        wizard.BackCommand.Execute(null);
        wizard.BackCommand.Execute(null);
        choice.ViewModel.ChooseCommand.Execute(choice.ViewModel.Options[0]);
        Assert.False(wizard.StepBar[4].IsSkipped);
        Assert.Equal("Step 2 of 5", wizard.StepProgressLabel);
    });
}
