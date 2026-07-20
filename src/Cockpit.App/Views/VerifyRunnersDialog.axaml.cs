using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

/// <summary>
/// The Verify-runners configuration dialog (AC-86): register, edit or remove the per-project command the visual
/// verify loop may run for a session. The command lives here, never with the agent, which can only trigger it.
/// </summary>
public partial class VerifyRunnersDialog : Window
{
    public VerifyRunnersDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
