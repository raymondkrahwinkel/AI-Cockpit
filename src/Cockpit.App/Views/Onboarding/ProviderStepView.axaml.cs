using Avalonia.Controls;

namespace Cockpit.App.Views.Onboarding;

// The wizard's provider page (AC-510[b]) — a plain `UserControl`; all state lives in `ViewModels.Onboarding.ProviderStepViewModel`.
public partial class ProviderStepView : UserControl
{
    public ProviderStepView()
    {
        InitializeComponent();
    }
}
