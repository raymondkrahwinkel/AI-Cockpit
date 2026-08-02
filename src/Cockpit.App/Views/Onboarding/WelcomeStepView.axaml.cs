using Avalonia.Controls;

namespace Cockpit.App.Views.Onboarding;

/// <summary>The wizard's first page (AC-509) — a plain <see cref="UserControl"/> with no view model of its own,
/// since it has nothing to bind besides the product name it resolves from <c>CockpitBrand</c>.</summary>
public partial class WelcomeStepView : UserControl
{
    public WelcomeStepView()
    {
        InitializeComponent();
    }
}
