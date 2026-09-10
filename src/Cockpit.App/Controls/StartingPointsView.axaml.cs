using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// AC-488: the starting-point gallery. No styled properties — unlike `SharedProjectCardView`, both hosts give it the
// same `ProjectsViewModel` as its data context, and everything it needs is on that.
public partial class StartingPointsView : UserControl
{
    public StartingPointsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
