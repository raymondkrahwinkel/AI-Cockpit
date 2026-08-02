using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

// The operator's read-only window on the agent line (AC-397): who said what to whom on this desk, which wakes were
// asked for and what became of them, what is claimed, what has been spent against the rate limit, and which panes
// the cockpit can see but has never heard from.
//
// Read-only is the whole point and is enforced by there being nothing else here: no send, no wake, no release. The
// operator observes this line from outside it — putting a reply button on this window would make them a participant
// in the path it exists to make visible.
public partial class AgentLineInspectorDialog : Window
{
    public AgentLineInspectorDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
