using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// Options dialog (#13): a categorised replacement for the sidebar's Options flyout, which had grown
/// too tall for a popup. Its <see cref="Window.DataContext"/> is the shared <c>CockpitViewModel</c>
/// passed in by <see cref="Cockpit.App.Services.SessionDialogService.ShowOptionsDialogAsync"/> — every
/// binding here is unchanged from the flyout, just re-hosted under tabs. Plugin-contributed Options tabs
/// (#14) are appended when the dialog opens, after the static tabs.
/// </summary>
public partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        _AppendPluginTabs();
    }

    // Each loaded plugin's Options tab is built from its factory and added after the static tabs; the
    // plugin owns the tab content. A missing view model or an empty registry simply adds nothing.
    private void _AppendPluginTabs()
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        foreach (var tab in cockpit.PluginOptionsTabs)
        {
            Tabs.Items.Add(new TabItem
            {
                Header = tab.Title,
                Content = new ScrollViewer { Content = tab.CreateView() },
            });
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
