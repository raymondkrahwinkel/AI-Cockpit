using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// AC-713: pure XAML, no logic of its own — see the XAML header comment for what this shares and why.
public partial class LoginFlowView : UserControl
{
    public LoginFlowView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
