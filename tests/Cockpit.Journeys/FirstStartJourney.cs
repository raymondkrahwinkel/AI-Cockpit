using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.ViewTests;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;

namespace Cockpit.Journeys;

// J9, the very first start: a machine that has never run the wizard opens it before the cockpit, asks how to begin,
// and "Start fresh" walks the other steps into the cockpit. The wizard is shown before the plugins' phase, like
// App's first-run route.
[Collection(JourneyCollection.Alone)]
public sealed class FirstStartJourney
{
    [Fact]
    public async Task AFreshMachine_AsksHowToBegin_AndStartingFresh_WalksTheWizardIntoTheCockpit()
    {
        await using var cockpit = JourneyHost.Desktop();
        var services = cockpit.Services;
        var state = services.GetRequiredService<IFirstRunWizardStateStore>();
        Assert.Null(await state.GetCompletedVersionAsync());

        var opened = new TaskCompletionSource<FirstRunWizardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = Window.WindowOpenedEvent.AddClassHandler<FirstRunWizardWindow>((wizard, _) => opened.TrySetResult(wizard));
        Task shown = Task.CompletedTask;
        await HeadlessAvalonia.RunAsync(() =>
        {
            shown = services.GetRequiredService<IFirstRunWizard>().ShowAsync();
            return Task.CompletedTask;
        });
        var window = await opened.Task.WaitAsync(Until.Ceiling);
        var wizard = HeadlessAvalonia.Run(() => Assert.IsType<FirstRunWizardViewModel>(window.DataContext));

        // The choice step is the second page, with the host's own two ways and no plugin's yet.
        var choice = services.GetServices<IFirstRunWizardStep>().OfType<StartChoiceStep>().Single();
        var (steps, titles, label) = HeadlessAvalonia.Run(() =>
        {
            wizard.NextCommand.Execute(null);
            return (wizard.StepBar.Select(item => item.Title).ToList(), choice.ViewModel.Options.Select(option => option.Title).ToList(), wizard.StepProgressLabel);
        });
        Assert.Equal(["What this is", "Getting started", "What you have", "Your assistant", "What you work on"], steps);
        Assert.Equal(["Start fresh", "Restore a backup file"], titles);
        Assert.Equal("Step 2 of 5", label);

        // Start fresh is the pick nobody has to make: Next carries on through every step, and the last Next ends it.
        await HeadlessAvalonia.RunAsync(() =>
        {
            while (!wizard.IsLastStep)
            {
                wizard.NextCommand.Execute(null);
            }

            wizard.NextCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            return Task.CompletedTask;
        });
        await shown.WaitAsync(Until.Ceiling);
        Assert.Equal(FirstRunWizardVersion.Current, await state.GetCompletedVersionAsync());

        await cockpit.StartDesktopAsync();
        Assert.True(HeadlessAvalonia.Run(() => cockpit.Window.IsVisible));
    }
}
