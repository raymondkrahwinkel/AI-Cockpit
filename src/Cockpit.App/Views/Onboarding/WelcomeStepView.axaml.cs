using Avalonia.Controls;

namespace Cockpit.App.Views.Onboarding;

// The wizard's first page (AC-509) — a plain `UserControl` with no view model of its own,
// since it has nothing to bind besides the product name it resolves from `CockpitBrand`.
public partial class WelcomeStepView : UserControl
{
    public WelcomeStepView()
    {
        InitializeComponent();
    }
}
