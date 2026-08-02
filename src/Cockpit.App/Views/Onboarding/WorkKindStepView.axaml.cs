using Avalonia;
using Avalonia.Controls;
using Cockpit.App.ViewModels.Onboarding;

namespace Cockpit.App.Views.Onboarding;

// The work-kind step's content (AC-511). Loads the store catalogues once it is on screen — the wizard
// builds every step's content up front, and reading the network for a step nobody has reached yet is work spent
// on a screen that may be skipped.
public partial class WorkKindStepView : UserControl
{
    private bool _loadStarted;

    public WorkKindStepView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_loadStarted || DataContext is not WorkKindStepViewModel viewModel)
        {
            return;
        }

        _loadStarted = true;
        _ = viewModel.LoadAsync();
    }
}
