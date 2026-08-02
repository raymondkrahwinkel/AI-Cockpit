using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels.Onboarding;

namespace Cockpit.App.Views.Onboarding;

/// <summary>
/// The first-run wizard shell (AC-509): a step bar plus Back/Next/Skip over whatever steps the current build has
/// registered. Closes itself when the view model asks — Skip, or Next on the last step — and
/// <see cref="Services.FirstRunWizardService"/> is what marks the wizard complete once this window closes, however
/// it closes, so the operator dismissing it some other way (Escape, the caption's Close button) still counts.
/// </summary>
public partial class FirstRunWizardWindow : Window
{
    public FirstRunWizardWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, titleBar: CockpitTitleBar.Window);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is FirstRunWizardViewModel viewModel)
            {
                viewModel.RequestClose += (_, _) => Close();
            }
        };
    }
}
