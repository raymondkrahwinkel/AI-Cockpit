using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

/// <summary>
/// Options dialog (#13): a categorised replacement for the sidebar's Options flyout, which had grown
/// too tall for a popup. Its <see cref="Window.DataContext"/> is the shared <c>CockpitViewModel</c>
/// passed in by <see cref="Cockpit.App.Services.SessionDialogService.ShowOptionsDialogAsync"/>. Plugin
/// settings (#14) are no longer top-level tabs — each plugin is configured from the gear next to it in the
/// Plugins tab, which opens the plugin's own settings dialog.
/// </summary>
public partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    /// <summary>Opens straight to the Plugins tab. Looked up by header text rather than a hardcoded index, so a future tab reorder can't silently select the wrong one.</summary>
    public void SelectPluginsTab()
    {
        foreach (var item in Tabs.Items)
        {
            if (item is TabItem { Header: "Plugins" })
            {
                Tabs.SelectedItem = item;
                return;
            }
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
