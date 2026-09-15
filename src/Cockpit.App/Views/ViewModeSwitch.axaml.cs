using Avalonia.Controls;

namespace Cockpit.App.Views;

// AC-1301: the Simple/Panels switch. No view model of its own — it inherits the `CockpitViewModel` of
// whichever column it stands in, which is what keeps the two placements one control instead of two copies.
public partial class ViewModeSwitch : UserControl
{
    public ViewModeSwitch()
    {
        InitializeComponent();
    }
}
