using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.App.ViewModels;
using System.Collections.Specialized;

namespace Cockpit.App.Views;

// AC-1474: remote plugins are administered here; credentials stay on the server and never enter this page.
public sealed class ServerPluginsPage : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private ServerAdminViewModel? _admin;

    public ServerPluginsPage()
    {
        var heading = new TextBlock { FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold };
        heading.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("PluginsCount") { StringFormat = "Plugins on the server · {0}" });
        var install = new Button { Content = "Install from store", HorizontalAlignment = HorizontalAlignment.Left };
        install.Bind(Button.CommandProperty, new Avalonia.Data.Binding("InstallPluginCommand"));
        var update = new Button { Content = "Update 1", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
        var restart = new TextBlock { Text = "Plugin changes take effect after the server restarts.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var status = new TextBlock();
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Status"));
        Content = new StackPanel { Margin = new Thickness(24, 20), Spacing = 12, Children = { heading, install, update, _rows, restart, status } };
        DataContextChanged += (_, _) => _SetAdmin();
    }

    private void _SetAdmin()
    {
        if (_admin is not null)
        {
            _admin.Plugins.CollectionChanged -= _PluginsChanged;
        }

        _admin = DataContext as ServerAdminViewModel;
        if (_admin is not null)
        {
            _admin.Plugins.CollectionChanged += _PluginsChanged;
        }

        _ShowRows();
    }

    private void _PluginsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ShowRows();

    private void _ShowRows()
    {
        _rows.Children.Clear();
        if (DataContext is not ServerAdminViewModel admin)
        {
            return;
        }

        foreach (var row in admin.Plugins)
        {
            var state = new TextBlock();
            state.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ServerPluginRowViewModel.State)) { Source = row });
            var enable = new Button { Content = "Enable / disable", Command = admin.TogglePluginCommand, CommandParameter = row };
            var remove = new Button { Content = "Remove", Command = admin.RemovePluginCommand, CommandParameter = row };
            var actions = new StackPanel { Children = { enable } };
            Grid.SetColumn(actions, 1);
            var removal = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Children = { remove } };
            Grid.SetColumn(removal, 2);
            _rows.Children.Add(new Border
            {
                Padding = new Thickness(12),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                    Children =
                    {
                        new StackPanel { Children = { new TextBlock { Text = $"{row.Name} {row.Version}", FontWeight = Avalonia.Media.FontWeight.SemiBold }, state } },
                        actions,
                        removal,
                    },
                },
            });
        }
    }
}
