using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

/// <summary>
/// First-load consent dialog (#14): shows what a plugin is (name/version/author/path/SHA-256) and that it
/// runs unsandboxed with the operator's rights, before it is enabled. Returns <c>true</c> from
/// <c>ShowDialog</c> only when the operator explicitly clicks Enable.
/// </summary>
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
