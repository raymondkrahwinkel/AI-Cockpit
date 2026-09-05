using Avalonia.Controls;

namespace Cockpit.App.Views.Onboarding;

// The wizard's assistant page (AC-585) — a plain `UserControl`; all state lives in `ViewModels.Onboarding.AssistantStepViewModel`.
public partial class AssistantStepView : UserControl
{
    public AssistantStepView()
    {
        InitializeComponent();
    }
}
