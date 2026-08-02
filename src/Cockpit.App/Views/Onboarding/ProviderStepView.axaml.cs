using Avalonia.Controls;

namespace Cockpit.App.Views.Onboarding;

/// <summary>The wizard's provider page (AC-510[b]) — a plain <see cref="UserControl"/>; all state lives in <see cref="ViewModels.Onboarding.ProviderStepViewModel"/>.</summary>
public partial class ProviderStepView : UserControl
{
    public ProviderStepView()
    {
        InitializeComponent();
    }
}
