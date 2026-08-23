using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

// AC-397: read-only window on the agent line — who said what, wakes, claims, rate-limit spend.
// Enforced by having nothing else here (no send/wake/release): the operator observes this line
// from outside it, a reply button would make them a participant in the path it exists to show.
public partial class AgentLineInspectorDialog : Window
{
    public AgentLineInspectorDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
