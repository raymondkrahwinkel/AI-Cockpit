using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// AC-1457: pure XAML over ServerHealthViewModel.
public partial class ServerHealthView : UserControl
{
    public ServerHealthView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
