using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views.Onboarding;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// The shell window's own responsibility (AC-509): close itself when the view model says so — Skip, or Next on
/// the last step — whichever one fired it. <see cref="Cockpit.App.Services.FirstRunWizardService"/> is what turns that close
/// into the persisted completion; that half is covered by <c>FirstRunWizardStateStoreTests</c> instead, since
/// reaching the window <c>ShowAsync</c> builds internally is not something a caller of that service can do.
/// </summary>
[Collection("avalonia")]
public class FirstRunWizardWindowTests
{
    // Skip and Next on the last step each close their own window.
    [Fact]
    public void SkipCommand_AndNextOnTheLastStep_EachCloseTheWindow() => HeadlessAvalonia.Run(() =>
    {
        foreach (var close in new Action<FirstRunWizardViewModel>[] { viewModel => viewModel.SkipCommand.Execute(null), viewModel => viewModel.NextCommand.Execute(null) })
        {
            var viewModel = new FirstRunWizardViewModel([new StubFirstRunWizardStep(0, "What this is", isSkipped: false)]);
            var window = new FirstRunWizardWindow { DataContext = viewModel };
            window.Show();

            var closed = false;
            window.Closed += (_, _) => closed = true;

            close(viewModel);

            Assert.True(closed);
        }
    });
}
