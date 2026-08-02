using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

// First-load consent dialog (#14): shows what a plugin is (name/version/author/path/SHA-256) and that it
// runs unsandboxed with the operator's rights, before it is enabled. Returns `true` from
// `ShowDialog` only when the operator explicitly clicks Enable.
public partial class PluginConsentDialog : Window
{
    public PluginConsentDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnEnable(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
