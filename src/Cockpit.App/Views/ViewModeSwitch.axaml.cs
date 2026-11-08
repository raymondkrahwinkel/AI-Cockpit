using Avalonia.Controls;

namespace Cockpit.App.Views;

// Inherits the window's CockpitViewModel so both modes share the same selection.
public partial class ViewModeSwitch : UserControl
{
    public ViewModeSwitch()
    {
        InitializeComponent();
    }
}
