using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels.Onboarding;

namespace Cockpit.App.Views.Onboarding;

// AC-509: first-run wizard shell over whatever steps are registered. Closes itself on the view
// model's request (Skip, or Next on the last step); FirstRunWizardService marks it complete on
// close however it closes, so Escape or the caption button count too.
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
