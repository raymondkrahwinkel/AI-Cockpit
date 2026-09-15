using Avalonia.Controls;

namespace Cockpit.App.Views;

// The left column's footer (Plugin store, Menu, Help). No view model of its own — like ViewModeSwitch it
// inherits the `CockpitViewModel` of whichever column it stands in, so both stands share one control.
public partial class SidebarFooter : UserControl
{
    public SidebarFooter()
    {
        InitializeComponent();
    }
}
